// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Hangfire
{
    /// <summary>
    /// Names of the <see cref="ActivitySource"/> and <see cref="Meter"/> that Hangfire uses for
    /// traces and metrics after <see cref="GlobalConfigurationExtensions.UseTelemetry"/> is called.
    /// With OpenTelemetry, subscribe with <c>AddSource(HangfireTelemetry.ActivitySourceName)</c>
    /// and <c>AddMeter(HangfireTelemetry.MeterName)</c>.
    /// </summary>
    public static class HangfireTelemetry
    {
        /// <summary>
        /// The name of the <see cref="ActivitySource"/> that creates "create" (producer) spans
        /// when a background job is created, and "process" (consumer) spans when it is performed.
        /// </summary>
        public const string ActivitySourceName = "Hangfire";

        /// <summary>
        /// The name of the <see cref="Meter"/> that reports the <c>hangfire.job.created</c>,
        /// <c>hangfire.job.duration</c> and <c>hangfire.job.active</c> instruments.
        /// </summary>
        public const string MeterName = "Hangfire";

        internal static readonly ActivitySource ActivitySource = new ActivitySource(
            ActivitySourceName,
            typeof(HangfireTelemetry).Assembly.GetName().Version?.ToString());

        internal static readonly Meter Meter = new Meter(
            MeterName,
            typeof(HangfireTelemetry).Assembly.GetName().Version?.ToString());

        internal static readonly Counter<long> JobsCreated = Meter.CreateCounter<long>(
            "hangfire.job.created",
            unit: "{job}",
            description: "Number of background jobs created.");

        internal static readonly Histogram<double> JobDuration = CreateJobDurationHistogram();

        internal static readonly UpDownCounter<long> ActiveJobs = Meter.CreateUpDownCounter<long>(
            "hangfire.job.active",
            unit: "{job}",
            description: "Number of background jobs being performed right now.");

        private static Histogram<double> CreateJobDurationHistogram()
        {
            const string name = "hangfire.job.duration";
            const string unit = "s";
            const string description = "Duration of background job executions, including server filters.";

#if NET9_0_OR_GREATER
            return Meter.CreateHistogram(name, unit, description, tags: null, advice: new InstrumentAdvice<double>
            {
                // Background jobs run from milliseconds to hours, the default boundaries are too short
                HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900, 3600]
            });
#else
            return Meter.CreateHistogram<double>(name, unit, description);
#endif
        }
    }
}
#endif
