#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift
{
    /// <summary>
    /// Options for the logging scope that servers added with <c>AddNightShiftServer</c> open around every job.
    /// Change them with <see cref="NightShiftJobLoggingServiceCollectionExtensions.ConfigureNightShiftJobLogging"/>.
    /// </summary>
    public sealed class NightShiftJobLoggingOptions
    {
        /// <summary>
        /// Gets or sets whether the server calls <see cref="ILogger.BeginScope{TState}"/> around each job, so every
        /// entry logged while the job runs, by the job's own <see cref="ILogger{TCategoryName}"/> too, and while the
        /// worker then changes the job's state (a retry warning, for example), carries
        /// <c>JobId</c>, <c>JobName</c> (<c>{Type}.{Method}</c>) and, when the job has them, <c>RecurringJobId</c>
        /// and <c>RetryCount</c>. The default is <c>true</c>.
        /// </summary>
        public bool BeginScope { get; set; } = true;
    }

    /// <summary>
    /// Configures the logging scope that servers added with <c>AddNightShiftServer</c> open around every job.
    /// </summary>
    public static class NightShiftJobLoggingServiceCollectionExtensions
    {
        /// <summary>
        /// Changes the <see cref="NightShiftJobLoggingOptions"/> of the servers added with <c>AddNightShiftServer</c>
        /// in this service collection, for example <c>services.ConfigureNightShiftJobLogging(o => o.BeginScope = false)</c>.
        /// It can be called before or after <c>AddNightShiftServer</c>; several calls change the same options.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Changes the options.</param>
        public static IServiceCollection ConfigureNightShiftJobLogging(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<NightShiftJobLoggingOptions> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            NightShiftJobLoggingOptions? options = null;
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType == typeof(NightShiftJobLoggingOptions) &&
                    descriptor.ImplementationInstance is NightShiftJobLoggingOptions registered)
                {
                    options = registered;
                }
            }

            if (options == null)
            {
                options = new NightShiftJobLoggingOptions();
                services.AddSingleton(options);
            }

            configure(options);
            return services;
        }
    }

    /// <summary>
    /// Adds the server filter that opens the job's logging scope to a server's filter provider.
    /// </summary>
    internal static class JobLoggingScope
    {
        internal const string LoggerName = "Masterly.NightShift.Jobs";

        /// <summary>
        /// Returns the filter provider a server created from <paramref name="services"/> should use: the given one
        /// with the scope filter in front of its filters, or the given one unchanged when the scope is turned off or
        /// there is no <see cref="ILoggerFactory"/>.
        /// </summary>
        [CanBeNull]
        public static IJobFilterProvider? AddTo([CanBeNull] IJobFilterProvider? filterProvider, [NotNull] IServiceProvider services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            // Options registered as a singleton may be passed to the factory again; one scope per job is enough
            if (filterProvider is ScopeFilterProvider) return filterProvider;

            var options = services.GetService<NightShiftJobLoggingOptions>();
            if (options != null && !options.BeginScope) return filterProvider;

            var loggerFactory = services.GetService<ILoggerFactory>();
            if (loggerFactory == null) return filterProvider;

            return new ScopeFilterProvider(
                filterProvider ?? JobFilterProviders.Providers,
                new ScopeFilter(loggerFactory.CreateLogger(LoggerName)));
        }

        private sealed class ScopeFilterProvider : IJobFilterProvider
        {
            private readonly IJobFilterProvider _inner;
            private readonly JobFilter _filter;

            public ScopeFilterProvider(IJobFilterProvider inner, ScopeFilter filter)
            {
                _inner = inner;
                _filter = new JobFilter(filter, JobFilterScope.Global, null);
            }

            public IEnumerable<JobFilter> GetFilters(Job job)
            {
                // First, so the scope also covers the other server filters and what they log
                var filters = new List<JobFilter> { _filter };
                filters.AddRange(_inner.GetFilters(job));
                return filters;
            }
        }

        /// <summary>
        /// Opens the scope in <c>OnPerforming</c>, on the worker thread before the job runs. The scope lives in the
        /// execution context, so code the job runs after an <c>await</c> logs inside it too. In <c>OnPerformed</c>
        /// the scope is handed to the worker, which closes it after the state change that follows the job, so what
        /// state filters log about the job (a retry, a deadline) is inside it too. Without a worker (a performer
        /// called directly) it is closed in <c>OnPerformed</c>.
        /// </summary>
        private sealed class ScopeFilter : IServerFilter
        {
            private const string ScopeItem = "Masterly.NightShift.JobLoggingScope";

            // Worker.DisposeAfterStateChangeItem in Masterly.NightShift.Core: the worker disposes what is in this
            // collection after its state change
            private const string DisposeAfterStateChangeItem = "X_NS_DisposeAfterStateChange";

            private readonly ILogger _logger;

            public ScopeFilter(ILogger logger)
            {
                _logger = logger;
            }

            public void OnPerforming(PerformingContext context)
            {
                if (context == null) throw new ArgumentNullException(nameof(context));

                var scope = _logger.BeginScope(new ScopeState(context.BackgroundJob));
                if (scope != null)
                {
                    context.Items[ScopeItem] = scope;
                }
            }

            public void OnPerformed(PerformedContext context)
            {
                if (context == null) throw new ArgumentNullException(nameof(context));

                IDisposable? scope;
                ICollection<IDisposable>? disposeAfterStateChange;

                // A thread the job started may still add items (PerformContext.Console does, under this lock)
                lock (context.Items)
                {
                    scope = context.Items.TryGetValue(ScopeItem, out var value) ? value as IDisposable : null;
                    context.Items.Remove(ScopeItem);

                    disposeAfterStateChange = context.Items.TryGetValue(DisposeAfterStateChangeItem, out var collection)
                        ? collection as ICollection<IDisposable>
                        : null;
                }

                if (scope == null) return;

                if (disposeAfterStateChange != null && !disposeAfterStateChange.IsReadOnly)
                {
                    disposeAfterStateChange.Add(scope);
                }
                else
                {
                    scope.Dispose();
                }
            }
        }

        /// <summary>
        /// The scope's values. Providers that don't print scopes never enumerate it, so the values are read from the
        /// job only when needed, and never from the storage: the parameters come from the snapshot the worker read
        /// with the job.
        /// </summary>
        private sealed class ScopeState : IReadOnlyList<KeyValuePair<string, object>>
        {
            private readonly BackgroundJob _backgroundJob;
            private KeyValuePair<string, object>[]? _values;
            private string? _text;

            public ScopeState(BackgroundJob backgroundJob)
            {
                _backgroundJob = backgroundJob;
            }

            private KeyValuePair<string, object>[] Values => _values ??= ReadValues();

            public int Count => Values.Length;

            public KeyValuePair<string, object> this[int index] => Values[index];

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                return ((IEnumerable<KeyValuePair<string, object>>)Values).GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public override string ToString()
            {
                if (_text == null)
                {
                    var text = new StringBuilder();
                    foreach (var pair in Values)
                    {
                        if (text.Length > 0) text.Append(' ');
                        text.Append(pair.Key).Append(':').Append(pair.Value);
                    }

                    _text = text.ToString();
                }

                return _text;
            }

            private KeyValuePair<string, object>[] ReadValues()
            {
                var values = new List<KeyValuePair<string, object>>(4)
                {
                    new KeyValuePair<string, object>("JobId", _backgroundJob.Id)
                };

                var job = _backgroundJob.Job;
                if (job != null)
                {
                    values.Add(new KeyValuePair<string, object>("JobName", $"{job.Type.Name}.{job.Method.Name}"));
                }

                var recurringJobId = ReadParameter<string>("RecurringJobId");
                if (!String.IsNullOrEmpty(recurringJobId))
                {
                    values.Add(new KeyValuePair<string, object>("RecurringJobId", recurringJobId!));
                }

                var retryCount = ReadParameter<int>("RetryCount");
                if (retryCount > 0)
                {
                    values.Add(new KeyValuePair<string, object>("RetryCount", retryCount));
                }

                return values.ToArray();
            }

            private T? ReadParameter<T>(string name)
            {
                var snapshot = _backgroundJob.ParametersSnapshot;
                if (snapshot == null || !snapshot.TryGetValue(name, out var value) || value == null) return default;

                try
                {
                    return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Runs while something is being logged: a parameter that can't be read is left out
                    return default;
                }
            }
        }
    }
}
