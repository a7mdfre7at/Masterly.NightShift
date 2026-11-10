namespace Masterly.NightShift.Core.Tests;

public class JobCancellationTokenFacts
{
    [Fact]
    public void ShutdownToken_IsInCanceledState_WhenPassingTrueValue()
    {
        JobCancellationToken token = new(true);
        Assert.True(token.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void ThrowIfCancellationRequested_DoesNotThrowOnFalseValue()
    {
        JobCancellationToken token = new(false);

        // Does not throw
        token.ThrowIfCancellationRequested();
    }

    [Fact]
    public void ThrowIfCancellationRequested_ThrowsOnTrueValue()
    {
        JobCancellationToken token = new(true);

        Assert.Throws<OperationCanceledException>(
            token.ThrowIfCancellationRequested);
    }

    [Fact]
    public void Null_ReturnsNullValue()
    {
        Assert.Null(JobCancellationToken.Null);
    }
}
