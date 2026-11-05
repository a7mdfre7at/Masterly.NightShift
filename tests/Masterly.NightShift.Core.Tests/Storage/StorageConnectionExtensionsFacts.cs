using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Storage
{
    public class StorageConnectionExtensionsFacts
    {
        private readonly Mock<IStorageConnection> _connection;

        public StorageConnectionExtensionsFacts()
        {
            _connection = new Mock<IStorageConnection>();
        }

        [Fact]
        public void GivenRecurringJobIsCancelledWhenGetRecurringJobsThenNotGetStateData()
        {
            _connection.Setup(o => o.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string> { "1" });
            _connection.Setup(o => o.GetAllEntriesFromHash("recurring-job:1")).Returns(new Dictionary<string, string>
                {
                    { "Cron", "A"},
                    { "Job", @"{""Type"":""ConsoleApplication1.CommandHandler, ConsoleApplication1"",""Method"":""Handle"",""ParameterTypes"":""[\""string\""]"",""Arguments"":""[\""Text\""]""}"},
                    { "LastJobId", string.Empty}
                }).Verifiable();

            var result = _connection.Object.GetRecurringJobs().Single();

            Assert.Null(result.LastJobState);
            _connection.VerifyAll();
        }

        [Fact]
        public void AcquireDistributedJobLock_AcquiresALock_WithTheCorrectResource()
        {
            var timeout = TimeSpan.FromSeconds(5);

            _connection.Object.AcquireDistributedJobLock("some-id", timeout);

            _connection.Verify(x => x.AcquireDistributedLock(
                "job:some-id:state-lock",
                timeout));
        }

        [Fact]
        public void GetRecurringJobs_WithGivenIdentifiers_QueriesForCorrespondingJobs()
        {
            // Act
            var result = _connection.Object.GetRecurringJobs(new[] { "a", "b" });

            // Assert
            Assert.Equal(2, result.Count);
            Assert.True(result[0].Removed);
            Assert.True(result[1].Removed);

            _connection.Verify(x => x.GetAllEntriesFromHash("recurring-job:a"), Times.Once);
            _connection.Verify(x => x.GetAllEntriesFromHash("recurring-job:b"), Times.Once);
        }

	    [Fact]
	    public void GetRecurringJobsWithNullDateTimeHashValues() 
		{
			_connection.Setup(o => o.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string> { "1" });
		    _connection.Setup(o => o.GetAllEntriesFromHash("recurring-job:1")).Returns(new Dictionary<string, string>
		    {
			    { "Cron", "A"},
			    { "Job", @"{""Type"":""ConsoleApplication1.CommandHandler, ConsoleApplication1"",""Method"":""Handle"",""ParameterTypes"":""[\""string\""]"",""Arguments"":""[\""Text\""]""}"},
				{ "NextExecution", null },
			    { "LastExecution", null },
			    { "CreatedAt", null }
		    }).Verifiable();

			var result = _connection.Object.GetRecurringJobs();
			Assert.Null(result[0].LastExecution);
			Assert.Null(result[0].NextExecution);
			Assert.Null(result[0].CreatedAt);
			_connection.VerifyAll();
		}

        [Theory]
        [InlineData("0", MisfireHandlingMode.Relaxed)]
        [InlineData("1", MisfireHandlingMode.Strict)]
        [InlineData("2", MisfireHandlingMode.Ignorable)]
        public void GetRecurringJobs_ReadsTheMisfireHandlingMode(string stored, MisfireHandlingMode expected)
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:a"))
                .Returns(new Dictionary<string, string> { { "Cron", "* * * * *" }, { "Misfire", stored } });

            var result = _connection.Object.GetRecurringJobs(new[] { "a" }).Single();

            Assert.Equal(expected, result.MisfireHandling);
        }

        [Theory]
        [InlineData("42")]
        [InlineData("-1")]
        [InlineData("strict")]
        [InlineData("")]
        public void GetRecurringJobs_FallsBackToRelaxed_ForUnknownMisfireValues(string stored)
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:a"))
                .Returns(new Dictionary<string, string> { { "Cron", "* * * * *" }, { "Misfire", stored } });

            var result = _connection.Object.GetRecurringJobs(new[] { "a" }).Single();

            Assert.Equal(MisfireHandlingMode.Relaxed, result.MisfireHandling);
        }

        [Fact]
        public void GetRecurringJobs_UsesRelaxedMisfireHandling_WhenTheFieldIsMissing()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:a"))
                .Returns(new Dictionary<string, string> { { "Cron", "* * * * *" } });

            var result = _connection.Object.GetRecurringJobs(new[] { "a" }).Single();

            Assert.Equal(MisfireHandlingMode.Relaxed, result.MisfireHandling);
            Assert.False(result.Paused);
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("true", false)]
        public void GetRecurringJobs_ReadsThePausedFlag(string stored, bool expected)
        {
            _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:a"))
                .Returns(new Dictionary<string, string> { { "Cron", "* * * * *" }, { "Paused", stored } });

            var result = _connection.Object.GetRecurringJobs(new[] { "a" }).Single();

            Assert.Equal(expected, result.Paused);
        }
	}
}
