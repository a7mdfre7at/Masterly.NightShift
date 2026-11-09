#nullable enable

using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>
/// What a bulk operation did, such as <see cref="BatchJob.Cancel(string, TimeSpan?)"/>,
/// <see cref="BatchJob.RequeueFailed(string, TimeSpan?)"/> or
/// <see cref="JobStorageExtensions.RequeueFailedJobs(JobStorage, Func{FailedJobDto, bool}, TimeSpan?)"/>.
/// </summary>
/// <param name="Processed">The number of jobs whose state was changed.</param>
/// <param name="Remaining">
/// <c>0</c> when the operation is complete. Otherwise an upper bound of the jobs another call would change,
/// or <c>null</c> when the storage can't count them.
/// </param>
/// <param name="Complete">
/// <c>false</c> when the operation stopped at its time budget before reaching every job. Calling it again goes
/// on with the jobs that are left.
/// </param>
public sealed record BulkJobResult(int Processed, long? Remaining, bool Complete);
