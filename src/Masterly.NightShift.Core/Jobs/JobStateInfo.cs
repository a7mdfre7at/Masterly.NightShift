#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// A job's current state, as <see cref="JobStorageExtensions.GetJobState(JobStorage, string)"/> reads it.
    /// </summary>
    public sealed class JobStateInfo
    {
        /// <summary>Initializes the state.</summary>
        /// <param name="name">The state's name, such as <c>Succeeded</c>.</param>
        /// <param name="reason">The reason given for the state, or <c>null</c>.</param>
        /// <param name="data">The state's data.</param>
        /// <param name="jobCreatedAt">When the job was created, in UTC.</param>
        public JobStateInfo([NotNull] string name, [CanBeNull] string? reason, [NotNull] IReadOnlyDictionary<string, string> data, DateTime jobCreatedAt)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Reason = reason;
            Data = data ?? throw new ArgumentNullException(nameof(data));
            JobCreatedAt = jobCreatedAt;
        }

        /// <summary>The state's name, such as <c>Enqueued</c>, <c>Succeeded</c> or <c>Failed</c>.</summary>
        [NotNull]
        public string Name { get; }

        /// <summary>The reason given for the state, or <c>null</c>.</summary>
        [CanBeNull]
        public string? Reason { get; }

        /// <summary>
        /// The state's data, by name (ignoring case), as the state serialized it: for example <c>SucceededAt</c> and
        /// <c>Result</c> for a succeeded job, <c>ExceptionType</c> and <c>ExceptionMessage</c> for a failed one.
        /// </summary>
        [NotNull]
        public IReadOnlyDictionary<string, string> Data { get; }

        /// <summary>When the job was created, in UTC.</summary>
        public DateTime JobCreatedAt { get; }
    }
}
