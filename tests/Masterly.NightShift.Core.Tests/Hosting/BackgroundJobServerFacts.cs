using System;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Client;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable ObjectCreationAsStatement

namespace Masterly.NightShift.Core.Tests
{
    public class BackgroundJobServerFacts
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

        private readonly Mock<JobStorage> _jobStorage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _storageConnection = new Mock<IStorageConnection>();
        private readonly BackgroundJobServerOptions _serverOptions = new BackgroundJobServerOptions
        {
            ServerName = "facts-server",
            WorkerCount = 2,
            Queues = new[] { "critical", "default" },
            IsLightweightServer = true,
            ShutdownTimeout = TimeSpan.FromSeconds(15),
        };

        public BackgroundJobServerFacts()
        {
            _jobStorage.Setup(x => x.GetConnection()).Returns(_storageConnection.Object);

            // Behaves like a real storage: waits for a job until the server stops
            _storageConnection
                .Setup(x => x.FetchNextJob(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
                .Returns<string[], CancellationToken>((_, token) =>
                {
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                    return null;
                });
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new BackgroundJobServer(_serverOptions, (JobStorage)null));

            Assert.Equal("storage", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenOptionsAreNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new BackgroundJobServer(null, _jobStorage.Object));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenAdditionalProcessesAreNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new BackgroundJobServer(_serverOptions, _jobStorage.Object, null));

            Assert.Equal("additionalProcesses", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenOnlySomeCustomServicesAreGiven()
        {
            var factory = new Mock<IBackgroundJobFactory>();
            var performer = new Mock<IBackgroundJobPerformer>();
            var stateChanger = new Mock<IBackgroundJobStateChanger>();

            Assert.Equal("performer", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
                _serverOptions, _jobStorage.Object, Enumerable.Empty<IBackgroundProcess>(), null, null, factory.Object, null, stateChanger.Object)).ParamName);
            Assert.Equal("factory", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
                _serverOptions, _jobStorage.Object, Enumerable.Empty<IBackgroundProcess>(), null, null, null, performer.Object, stateChanger.Object)).ParamName);
            Assert.Equal("stateChanger", Assert.Throws<ArgumentNullException>(() => new BackgroundJobServer(
                _serverOptions, _jobStorage.Object, Enumerable.Empty<IBackgroundProcess>(), null, null, factory.Object, performer.Object, null)).ParamName);
        }

        [Fact]
        public void Ctor_StartsTheServer_ThatAnnouncesItsQueuesAndWorkerCount_AndRemovesItselfOnShutdown()
        {
            var announced = new ManualResetEventSlim();
            string serverId = null;
            ServerContext serverContext = null;
            _storageConnection
                .Setup(x => x.AnnounceServer(It.IsAny<string>(), It.IsAny<ServerContext>()))
                .Callback<string, ServerContext>((id, context) =>
                {
                    serverId = id;
                    serverContext = context;
                    announced.Set();
                });

            using (var server = new BackgroundJobServer(_serverOptions, _jobStorage.Object, Enumerable.Empty<IBackgroundProcess>()))
            {
                Assert.True(announced.Wait(WaitTimeout, TestContext.Current.CancellationToken));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            Assert.StartsWith("facts-server:", serverId);
            Assert.Equal(new[] { "critical", "default" }, serverContext.Queues);
            Assert.Equal(2, serverContext.WorkerCount);
            _storageConnection.Verify(x => x.RemoveServer(serverId), Times.Once);
        }

        [Fact]
        public void Ctor_WithCustomServices_RunsTheAdditionalProcesses()
        {
            var executed = new ManualResetEventSlim();
            var process = new Mock<IBackgroundProcess>();
            process
                .Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>()))
                .Callback<BackgroundProcessContext>(context =>
                {
                    executed.Set();
                    context.StoppingToken.WaitHandle.WaitOne();
                });

            using (var server = new BackgroundJobServer(
                _serverOptions,
                _jobStorage.Object,
                new[] { process.Object },
                null,
                null,
                new Mock<IBackgroundJobFactory>().Object,
                new Mock<IBackgroundJobPerformer>().Object,
                new Mock<IBackgroundJobStateChanger>().Object))
            {
                Assert.True(executed.Wait(WaitTimeout, TestContext.Current.CancellationToken));

                server.SendStop();
                Assert.True(server.WaitForShutdown(WaitTimeout));
            }

            process.Verify(x => x.Execute(It.Is<BackgroundProcessContext>(context => context.ServerId.StartsWith("facts-server:"))));
        }
    }
}
