using System;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Logging
{
    public class LateBoundLogProviderFacts
    {
        [Fact, StaticLock]
        public void Logger_CreatedBeforeTheProviderIsSet_WritesToTheProviderSetLater()
        {
            var logger = LogProvider.GetLogger("Tests.Early");
            var recording = new RecordingLogProvider();

            using (UseProvider(recording))
            {
                logger.Warn("written after the provider was set");
            }

            var entry = Assert.Single(recording.Entries);
            Assert.Equal("Tests.Early", entry.Logger);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Equal("written after the provider was set", entry.Message);
        }

        [Fact, StaticLock]
        public void Logger_FollowsEveryLaterProviderChange()
        {
            var logger = LogProvider.GetLogger("Tests.Switching");
            var first = new RecordingLogProvider();
            var second = new RecordingLogProvider();

            using (UseProvider(first))
            {
                logger.Info("one");
                GlobalConfiguration.Configuration.UseLogProvider(second);
                logger.Info("two");
            }

            Assert.Equal("one", Assert.Single(first.Entries).Message);
            Assert.Equal("two", Assert.Single(second.Entries).Message);
        }

        [Fact, StaticLock]
        public void Logger_ReportsTheLevelAsDisabled_WhenTheProviderTurnsItOff()
        {
            var logger = LogProvider.GetLogger("Tests.NoOp");

            using (UseProvider(new RecordingLogProvider()))
            {
                GlobalConfiguration.Configuration.UseNoOpLogProvider();

                Assert.False(logger.IsErrorEnabled());
            }
        }

        [Fact, StaticLock]
        public void UseLogProvider_TakesOverAgain_AfterSetCurrentLogProviderReplacedIt()
        {
            var other = new RecordingLogProvider();
            var recording = new RecordingLogProvider();

            using (UseProvider(other))
            {
                LogProvider.SetCurrentLogProvider(other);
                GlobalConfiguration.Configuration.UseLogProvider(recording);

                LogProvider.GetLogger("Tests.Replaced").Error("message");
            }

            Assert.Empty(other.Entries);
            Assert.Equal("message", Assert.Single(recording.Entries).Message);
        }

        [Fact, StaticLock]
        public void Logger_DoesNotThrow_WhenTheProviderCannotCreateALogger()
        {
            var logger = LogProvider.GetLogger("Tests.Broken");
            var broken = new Mock<ILogProvider>();
            broken.Setup(x => x.GetLogger(It.IsAny<string>())).Throws(new ObjectDisposedException("LoggerFactory"));

            using (UseProvider(new RecordingLogProvider()))
            {
                GlobalConfiguration.Configuration.UseLogProvider(broken.Object);

                logger.Error("lost");
            }

            broken.Verify(x => x.GetLogger("Tests.Broken"));
        }

        [Fact, StaticLock]
        public void RetryFilter_CreatedBeforeTheProviderIsSet_LogsTheRetryToIt()
        {
            // Like the default global filter, it gets its logger when it is constructed
            var filter = new AutomaticRetryAttribute { Attempts = 1 };
            var context = new ElectStateContextMock();
            context.ApplyContext.BackgroundJob.Id = "42";
            context.ApplyContext.NewStateObject = new FailedState(new InvalidOperationException("boom"));
            var recording = new RecordingLogProvider();

            using (UseProvider(recording))
            {
                filter.OnStateElection(context.Object);
            }

            var entry = Assert.Single(recording.Entries, x => x.Logger == typeof(AutomaticRetryAttribute).FullName);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Contains("'42'", entry.Message);
            Assert.IsType<InvalidOperationException>(entry.Exception);
        }

        private static IDisposable UseProvider(RecordingLogProvider provider)
        {
            return provider.Use();
        }
    }
}
