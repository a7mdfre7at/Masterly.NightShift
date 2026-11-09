using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests.Stubs;

/// <summary>A host lifetime whose events the test raises by hand.</summary>
internal sealed class HostApplicationLifetimeStub : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;

    public void RaiseStarted() => _started.Cancel();
    public void RaiseStopping() => _stopping.Cancel();
    public void RaiseStopped() => _stopped.Cancel();

    public void StopApplication() => _stopping.Cancel();
}
