#nullable enable

namespace Masterly.NightShift;

/// <summary>
/// Thrown when a job configured with <see cref="ConcurrencyAction.Wait"/> doesn't get a slot before
/// the wait timeout expires. The job fails and the usual retry rules apply.
/// </summary>
/// <param name="resource">The throttled resource.</param>
/// <param name="message">The message.</param>
public class ConcurrencyLimitException(string resource, string message) : Exception(message)
{

    /// <summary>Gets the resource whose limit was reached.</summary>
    public string Resource { get; } = resource;
}
