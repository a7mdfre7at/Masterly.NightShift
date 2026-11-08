using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests.Hosting
{
    public class JobLoggingScopeFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly ScopeCapturingProvider _logs = new ScopeCapturingProvider();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AddNightShiftServer_RunsAnAsyncJob_InsideALoggingScope_ItsCodeAfterAnAwaitSees(bool useTaskScheduler)
        {
            using (var provider = BuildServices().BuildServiceProvider())
            {
                var job = Job.FromExpression<LoggingJob>(x => x.RunAsync());

                Perform(provider, job, Snapshot(("RecurringJobId", "\"nightly-report\""), ("RetryCount", "2")), useTaskScheduler);

                var scope = Assert.Single(_logs.ScopesOf("Inside the async job"));
                Assert.Equal("42", scope["JobId"]);
                Assert.Equal("LoggingJob.RunAsync", scope["JobName"]);
                Assert.Equal("nightly-report", scope["RecurringJobId"]);
                Assert.Equal(2, scope["RetryCount"]);
                Assert.Equal("JobId:42 JobName:LoggingJob.RunAsync RecurringJobId:nightly-report RetryCount:2", Assert.Single(_logs.ScopeTextsOf("Inside the async job")));
            }
        }

        [Fact]
        public void AddNightShiftServer_RunsASyncJob_InsideALoggingScope_AndClosesItAfterwards()
        {
            using (var provider = BuildServices().BuildServiceProvider())
            {
                Perform(provider, Job.FromExpression<LoggingJob>(x => x.Run()), Snapshot(), useTaskScheduler: true);
                provider.GetRequiredService<ILogger<JobLoggingScopeFacts>>().LogInformation("After the job");

                var scope = Assert.Single(_logs.ScopesOf("Inside the sync job"));
                Assert.Equal(new[] { "JobId", "JobName" }, scope.Keys);
                Assert.Empty(_logs.ScopesOf("After the job"));
            }
        }

        [Fact]
        public void AddNightShiftServer_KeepsTheScope_ForEntriesOfAFailingJob()
        {
            using (var provider = BuildServices().BuildServiceProvider())
            {
                var job = Job.FromExpression<LoggingJob>(x => x.FailAsync());

                Assert.Throws<JobPerformanceException>(() => Perform(provider, job, Snapshot(), useTaskScheduler: true));
                provider.GetRequiredService<ILogger<JobLoggingScopeFacts>>().LogInformation("After the job");

                Assert.Equal("42", Assert.Single(_logs.ScopesOf("Before failing"))["JobId"]);
                Assert.Empty(_logs.ScopesOf("After the job"));
            }
        }

        [Fact]
        public void AddNightShiftServer_KeepsTheScope_UntilTheWorkerChangedTheJobsState_AndClosesItAfterwards()
        {
            using (var provider = BuildServices().BuildServiceProvider())
            {
                var states = RunWorker(provider, Job.FromExpression<LoggingJob>(x => x.FailWithOneRetry()));
                provider.GetRequiredService<ILogger<JobLoggingScopeFacts>>().LogInformation("After the job");

                // AutomaticRetry logs its warning while the worker moves the failed job to Scheduled
                Assert.Equal(new[] { ProcessingState.StateName, ScheduledState.StateName }, states);
                var retryWarning = Assert.Single(_logs.ScopesOfMessagesStartingWith("Failed to process the job '42'"));
                Assert.Equal("42", retryWarning["JobId"]);
                Assert.Equal("LoggingJob.FailWithOneRetry", retryWarning["JobName"]);
                Assert.Empty(_logs.ScopesOf("After the job"));
            }
        }

        [Fact]
        public void AddNightShiftServer_DoesNotReadTheStorage_ForTheScope()
        {
            using (var provider = BuildServices().BuildServiceProvider())
            {
                Perform(provider, Job.FromExpression<LoggingJob>(x => x.RunAsync()), parametersSnapshot: null, useTaskScheduler: true);

                Assert.Equal(new[] { "JobId", "JobName" }, Assert.Single(_logs.ScopesOf("Inside the async job")).Keys);
                // Without a snapshot other filters may read their own parameters, but the scope reads none
                _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), "RecurringJobId"), Times.Never);
                _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), "RetryCount"), Times.Never);
            }
        }

        [Fact]
        public void ConfigureNightShiftJobLogging_TurnsTheScopeOff()
        {
            var services = BuildServices().ConfigureNightShiftJobLogging(options => options.BeginScope = false);

            using (var provider = services.BuildServiceProvider())
            {
                Assert.Same(provider.GetRequiredService<IJobFilterProvider>(), GetServerOptions(provider).FilterProvider);

                Perform(provider, Job.FromExpression<LoggingJob>(x => x.RunAsync()), Snapshot(), useTaskScheduler: true);

                Assert.Empty(_logs.ScopesOf("Inside the async job"));
            }
        }

        [Fact]
        public void ConfigureNightShiftJobLogging_ChangesTheSameOptions_WhenCalledTwice()
        {
            var services = new ServiceCollection()
                .ConfigureNightShiftJobLogging(options => options.BeginScope = false)
                .ConfigureNightShiftJobLogging(_ => { });

            using (var provider = services.BuildServiceProvider())
            {
                Assert.False(Assert.Single(provider.GetServices<NightShiftJobLoggingOptions>()).BeginScope);
            }
        }

        [Fact]
        public void AddNightShiftServer_OpensOneScope_WhenSharedServerOptionsAreUsedTwice()
        {
            var services = BuildServices(addServer: false)
                .AddSingleton(new BackgroundJobServerOptions())
                .AddNightShiftServer();

            using (var provider = services.BuildServiceProvider())
            {
                provider.GetServices<IHostedService>().ToList();
                provider.GetServices<IHostedService>().ToList();

                Perform(provider, Job.FromExpression<LoggingJob>(x => x.RunAsync()), Snapshot(), useTaskScheduler: true);

                Assert.Single(_logs.ScopesOf("Inside the async job"));
            }
        }

        [Fact]
        public void ConfigureNightShiftJobLogging_ThrowsAnException_WhenAnArgumentIsNull()
        {
            Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => NightShiftJobLoggingServiceCollectionExtensions.ConfigureNightShiftJobLogging(null, _ => { })).ParamName);
            Assert.Equal("configure", Assert.Throws<ArgumentNullException>(() => new ServiceCollection().ConfigureNightShiftJobLogging(null)).ParamName);
        }

        private IServiceCollection BuildServices(bool addServer = true)
        {
            var services = new ServiceCollection()
                .AddLogging(builder => builder.AddProvider(_logs))
                .AddTransient<LoggingJob>()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { });

            return addServer ? services.AddNightShiftServer(_ => { }) : services;
        }

        private void Perform(IServiceProvider provider, Job job, IReadOnlyDictionary<string, string> parametersSnapshot, bool useTaskScheduler)
        {
            var options = GetServerOptions(provider);
            var performer = new BackgroundJobPerformer(options.FilterProvider, options.Activator, useTaskScheduler ? TaskScheduler.Default : null);
            var backgroundJob = new BackgroundJob("42", job, DateTime.UtcNow, parametersSnapshot);
            var token = new Mock<IJobCancellationToken>();

            performer.Perform(new PerformContext(_storage.Object, _connection.Object, backgroundJob, token.Object));
        }

        /// <summary>Lets a worker of the server fetch, perform and finish job 42, and returns the states it set.</summary>
        private List<string> RunWorker(IServiceProvider provider, Job job)
        {
            var options = GetServerOptions(provider);
            var states = new List<string>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            transaction.Setup(x => x.SetJobState("42", It.IsAny<IState>())).Callback<string, IState>((_, state) => states.Add(state.Name));
            var fetchedJob = new Mock<IFetchedJob>();
            fetchedJob.Setup(x => x.JobId).Returns("42");

            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>())).Returns(fetchedJob.Object);
            _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            _connection.Setup(x => x.GetJobData("42")).Returns(() => new JobData
            {
                Job = job,
                State = states.Count == 0 ? EnqueuedState.StateName : states[states.Count - 1],
                CreatedAt = DateTime.UtcNow,
                ParametersSnapshot = new Dictionary<string, string>(),
            });

            var performer = new BackgroundJobPerformer(options.FilterProvider, options.Activator, TaskScheduler.Default);
            var worker = new Worker(new[] { "default" }, performer, new BackgroundJobStateChanger(options.FilterProvider));

            worker.Execute(new BackgroundProcessContext("server", _storage.Object, new Dictionary<string, object>(), Guid.NewGuid(), CancellationToken.None, CancellationToken.None, CancellationToken.None));
            return states;
        }

        private static BackgroundJobServerOptions GetServerOptions(IServiceProvider provider)
        {
            var server = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().First();

            // The options are private to the hosted service
            return (BackgroundJobServerOptions)typeof(BackgroundJobServerHostedService)
                .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(server);
        }

        private static Dictionary<string, string> Snapshot(params (string Name, string Value)[] parameters)
        {
            return parameters.ToDictionary(x => x.Name, x => x.Value);
        }

        public sealed class LoggingJob
        {
            private readonly ILogger<LoggingJob> _logger;

            public LoggingJob(ILogger<LoggingJob> logger)
            {
                _logger = logger;
            }

            public async Task RunAsync()
            {
                await Task.Yield();
                await Task.Delay(10).ConfigureAwait(false);
                _logger.LogInformation("Inside the async job");
            }

            public void Run()
            {
                _logger.LogInformation("Inside the sync job");
            }

            public async Task FailAsync()
            {
                await Task.Yield();
                _logger.LogInformation("Before failing");
                throw new InvalidOperationException("Boom");
            }

            [AutomaticRetry(Attempts = 1)]
            public void FailWithOneRetry()
            {
                throw new InvalidOperationException("Boom");
            }
        }

        private sealed class ScopeCapturingProvider : ILoggerProvider, ISupportExternalScope
        {
            private readonly ConcurrentQueue<(string Message, List<object> Scopes)> _entries = new ConcurrentQueue<(string, List<object>)>();
            private IExternalScopeProvider _scopes;

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

            public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

            public void Dispose()
            {
            }

            public IEnumerable<Dictionary<string, object>> ScopesOf(string message)
            {
                return _entries.Where(x => x.Message == message)
                    .SelectMany(x => x.Scopes)
                    .OfType<IEnumerable<KeyValuePair<string, object>>>()
                    .Select(x => x.ToDictionary(p => p.Key, p => p.Value));
            }

            public IEnumerable<Dictionary<string, object>> ScopesOfMessagesStartingWith(string prefix)
            {
                return _entries.Where(x => x.Message.StartsWith(prefix, StringComparison.Ordinal))
                    .SelectMany(x => x.Scopes)
                    .OfType<IEnumerable<KeyValuePair<string, object>>>()
                    .Select(x => x.ToDictionary(p => p.Key, p => p.Value));
            }

            public IEnumerable<string> ScopeTextsOf(string message)
            {
                return _entries.Where(x => x.Message == message).SelectMany(x => x.Scopes).Select(x => x.ToString());
            }

            private sealed class CapturingLogger : ILogger
            {
                private readonly ScopeCapturingProvider _provider;

                public CapturingLogger(ScopeCapturingProvider provider)
                {
                    _provider = provider;
                }

                public IDisposable BeginScope<TState>(TState state) => _provider._scopes?.Push(state);

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    var scopes = new List<object>();
                    _provider._scopes?.ForEachScope((scope, list) => list.Add(scope), scopes);
                    _provider._entries.Enqueue((formatter(state, exception), scopes));
                }
            }
        }
    }
}
