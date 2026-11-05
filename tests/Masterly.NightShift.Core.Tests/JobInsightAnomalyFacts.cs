using System;
using System.Linq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobInsightAnomalyFacts
    {
        private static JobInsight Create(long[] durations, long succeeded = 100, long failed = 0)
        {
            return new JobInsight("Type.Method", succeeded, failed, durations, DateTime.UtcNow, durations.LastOrDefault(), "1", "srv", false, "Generated");
        }

        [Fact]
        public void IsSlowingDown_WhenTheRecentMedianDoubles()
        {
            var durations = Enumerable.Repeat(1000L, 20).Concat(Enumerable.Repeat(2500L, 10)).ToArray();

            var insight = Create(durations);

            Assert.Equal(1000, insight.EarlierMedianMs);
            Assert.Equal(2500, insight.RecentMedianMs);
            Assert.True(insight.IsSlowingDown);
            Assert.True(insight.IsAnomalous);
        }

        [Fact]
        public void IsSlowingDown_IsFalse_WithoutEnoughSamples_OrForSubSecondRuns()
        {
            Assert.False(Create(new long[] { 100, 100, 100, 5000, 5000 }).IsSlowingDown);
            Assert.False(Create(Enumerable.Repeat(100L, 20).Concat(Enumerable.Repeat(400L, 10)).ToArray()).IsSlowingDown);
        }

        [Fact]
        public void HasHighFailureRate_NeedsTenRuns_AndAFifthFailing()
        {
            Assert.False(Create(new long[0], succeeded: 4, failed: 4).HasHighFailureRate);
            Assert.False(Create(new long[0], succeeded: 90, failed: 10).HasHighFailureRate);
            Assert.True(Create(new long[0], succeeded: 8, failed: 2).HasHighFailureRate);
        }

        [Fact]
        public void EstimateCompletion_UsesTheMedian_WithAtLeastThreeSamples()
        {
            var started = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            Assert.Null(Create(new long[] { 1000, 2000 }).EstimateCompletion(started));
            Assert.Equal(started.AddSeconds(2), Create(new long[] { 1000, 2000, 3000 }).EstimateCompletion(started));
        }

        [Fact]
        public void IsAnomalous_IsFalse_ForAHealthyMethod()
        {
            var insight = Create(Enumerable.Repeat(1000L, 30).ToArray(), succeeded: 100, failed: 1);

            Assert.False(insight.IsAnomalous);
        }
    }
}
