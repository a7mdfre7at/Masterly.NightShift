using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Handles background jobs described by a plain arguments object, so the code that creates a job only needs
/// the arguments: <c>client.Enqueue(new SendInvoice(42))</c>. Register the handler with
/// <see cref="JobHandlers.Add{THandler}"/>, or with <c>AddNightShiftJobHandler</c> in a service collection.
/// </summary>
/// <remarks>
/// The job that is stored is an ordinary call of the handler's method, so attributes on the handler class or
/// on its <see cref="HandleAsync"/> method (<see cref="QueueAttribute"/>, <see cref="AutomaticRetryAttribute"/>
/// and every other job filter) apply as they do to any job, and the dashboard shows the handler's name.
/// Implement <see cref="HandleAsync"/> as a public method: an explicit interface implementation can't be a job.
/// </remarks>
/// <typeparam name="TArgs">The type of the job's arguments. It is stored as JSON with the job.</typeparam>
public interface IJobHandler<TArgs>
{
    /// <summary>Runs the job.</summary>
    /// <param name="args">The arguments the job was created with.</param>
    /// <param name="context">The running job: its identifier, retry count, parameters and console.</param>
    /// <param name="cancellationToken">Canceled when the server shuts down.</param>
    /// <returns>A task that completes when the job is done.</returns>
    Task HandleAsync(TArgs args, PerformContext context, CancellationToken cancellationToken);
}
