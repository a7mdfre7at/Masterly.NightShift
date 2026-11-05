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


#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.States
{
    /// <summary>
    /// How a fan-in continuation waits for its parents.
    /// </summary>
    public enum FanInMode
    {
        /// <summary>The continuation starts once every parent has finished (and satisfied the options).</summary>
        All,

        /// <summary>The continuation starts as soon as one parent has finished and satisfied the options.</summary>
        Any,
    }

    /// <summary>
    /// The state of a job that waits for a <em>set</em> of parent jobs: fan-out/fan-in without a batch package.
    /// Shares the <c>Awaiting</c> state name with <see cref="AwaitingState"/>, so these jobs appear on the same
    /// dashboard page and use the same state handler, but they are driven by
    /// <see cref="FanInContinuationsSupportAttribute"/> instead of <see cref="ContinuationsSupportAttribute"/>.
    /// </summary>
    public sealed class AwaitingJobsState : IState
    {
        /// <summary>The state name, identical to <see cref="AwaitingState.StateName"/>.</summary>
        public static readonly string StateName = AwaitingState.StateName;

        /// <summary>Creates the state for jobs waiting for all the given parents to succeed.</summary>
        public AwaitingJobsState([NotNull] IEnumerable<string> parentIds)
            : this(parentIds, new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All)
        {
        }

        /// <summary>Creates the state.</summary>
        /// <param name="parentIds">Identifiers of the parent jobs. Duplicates are removed.</param>
        /// <param name="nextState">The state the job moves to when the condition is met.</param>
        /// <param name="options">Which parent outcomes count as satisfying the condition.</param>
        /// <param name="mode">Whether every parent or any parent has to satisfy the condition.</param>
        [JsonConstructor]
        public AwaitingJobsState(
            [NotNull] IEnumerable<string> parentIds,
            [NotNull] IState nextState,
            JobContinuationOptions options,
            FanInMode mode)
        {
            if (parentIds == null) throw new ArgumentNullException(nameof(parentIds));
            if (nextState == null) throw new ArgumentNullException(nameof(nextState));

            ParentIds = parentIds.Where(static x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            if (ParentIds.Count == 0) throw new ArgumentException("At least one parent job identifier is required.", nameof(parentIds));

            NextState = nextState;
            Options = options;
            Mode = mode;
        }

        /// <summary>The parent job identifiers.</summary>
        [NotNull]
        public IReadOnlyList<string> ParentIds { get; }

        /// <summary>The state applied once the condition is met.</summary>
        [NotNull]
        public IState NextState { get; }

        /// <summary>Which parent outcomes satisfy the condition.</summary>
        public JobContinuationOptions Options { get; }

        /// <summary>Whether all parents or any parent has to finish.</summary>
        public FanInMode Mode { get; }

        /// <inheritdoc />
        [JsonIgnore]
        public string Name => StateName;

        /// <inheritdoc />
        public string? Reason { get; set; }

        /// <inheritdoc />
        [JsonIgnore]
        public bool IsFinal => false;

        /// <inheritdoc />
        [JsonIgnore]
        public bool IgnoreJobLoadException => false;

        /// <inheritdoc />
        public Dictionary<string, string> SerializeData()
        {
            return new Dictionary<string, string>
            {
                { "ParentId", ParentIds[0] },
                { "ParentIds", String.Join(",", ParentIds) },
                { "Mode", Mode.ToString("G") },
                { "NextState", SerializationHelper.Serialize(NextState, SerializationOption.TypedInternal) },
                { "Options", Options.ToString("D") },
            };
        }

        /// <summary>
        /// Rebuilds the waiting condition from serialized state data, or returns <c>null</c> when the data belongs
        /// to an ordinary <see cref="AwaitingState"/>.
        /// </summary>
        public static AwaitingJobsState? FromStateData([NotNull] IDictionary<string, string> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (!data.TryGetValue("ParentIds", out var parentIds) || String.IsNullOrEmpty(parentIds))
            {
                return null;
            }

            var mode = data.TryGetValue("Mode", out var modeValue) && Enum.TryParse(modeValue, out FanInMode parsedMode)
                ? parsedMode
                : FanInMode.All;

            var options = data.TryGetValue("Options", out var optionsValue) && Enum.TryParse(optionsValue, out JobContinuationOptions parsedOptions)
                ? parsedOptions
                : JobContinuationOptions.OnlyOnSucceededState;

            IState? nextState = null;
            if (data.TryGetValue("NextState", out var serializedNextState))
            {
                nextState = SerializationHelper.Deserialize<IState>(serializedNextState, SerializationOption.TypedInternal);
            }

            return new AwaitingJobsState(parentIds.Split(','), nextState ?? new EnqueuedState(), options, mode);
        }
    }
}
