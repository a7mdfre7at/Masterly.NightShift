namespace Masterly.NightShift;

/// <summary>
/// Stands for the runner of delegate job functions, which lives in Masterly.NightShift.NetCore: the pending jobs of a
/// delegate function are recognized by this type name, its method and its first argument.
/// </summary>
public sealed class DelegateJobFunctionRunner
{
    public Task RunAsync(string name, string payload, CancellationToken cancellationToken)
    {
        _ = name;
        _ = payload;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
