using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// A background job that is a class and takes no arguments, so the code that creates it only names the class:
/// <c>client.Enqueue&lt;NightlyReport&gt;()</c>. Jobs that need data implement <see cref="IJobHandler{TArgs}"/>.
/// </summary>
/// <remarks>
/// The job that is stored is an ordinary call of the class's method, so attributes on the class or on its
/// <see cref="RunAsync"/> method apply as they do to any job, and the dashboard shows the class's name.
/// Implement <see cref="RunAsync"/> as a public method: an explicit interface implementation can't be a job.
/// </remarks>
public interface IJob
{
    /// <summary>Runs the job.</summary>
    /// <param name="context">The running job: its identifier, retry count, parameters and console.</param>
    /// <param name="cancellationToken">Canceled when the server shuts down.</param>
    /// <returns>A task that completes when the job is done.</returns>
    Task RunAsync(PerformContext context, CancellationToken cancellationToken);
}
