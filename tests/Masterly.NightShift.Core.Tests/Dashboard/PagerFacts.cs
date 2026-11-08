using System;
using Masterly.NightShift.Dashboard;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class PagerFacts
    {
        [Theory]
        [InlineData(0, 20, 20)]
        [InlineData(50, 20, 50)]
        [InlineData(1000, 20, 1000)]
        [InlineData(5000, 20, 1000)]
        [InlineData(Int32.MaxValue, 20, 1000)]
        [InlineData(0, 0, 10)]
        [InlineData(0, 5000, 1000)]
        public void RecordsPerPage_IsTheRequestedCount_OrTheDefault_AtMostOneThousand(int perPage, int defaultPerPage, int expected)
        {
            var pager = new Pager(0, perPage, defaultPerPage, 100000);

            Assert.Equal(expected, pager.RecordsPerPage);
        }

        [Fact]
        public void FromRecord_LeavesRoomForAPage_SoTheLastRecordDoesNotOverflow()
        {
            var pager = new Pager(Int32.MaxValue, 1000, 20, 3_000_000_000L);

            // Pages ask storages for FromRecord..FromRecord + RecordsPerPage - 1
            Assert.True(pager.FromRecord + pager.RecordsPerPage - 1 > pager.FromRecord);
            Assert.Equal(Int32.MaxValue - 1000, pager.FromRecord);
        }

        [Fact]
        public void FromRecord_IsZero_WhenNegative()
        {
            var pager = new Pager(-5, 20, 100);

            Assert.Equal(0, pager.FromRecord);
            Assert.Equal(1, pager.CurrentPage);
        }

        [Fact]
        public void RecordsPerPageUrl_IsCapped_LikeThePage()
        {
            var pager = new Pager(0, 20, 100) { BasePageUrl = "/jobs/failed" };

            Assert.Equal("/jobs/failed?from=0&count=1000", pager.RecordsPerPageUrl(5000));
            Assert.Equal("/jobs/failed?from=0&count=50", pager.RecordsPerPageUrl(50));
        }
    }
}
