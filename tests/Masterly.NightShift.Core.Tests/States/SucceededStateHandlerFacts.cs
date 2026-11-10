namespace Masterly.NightShift.Core.Tests.States;

public class SucceededStateHandlerFacts
{
    private readonly ApplyStateContextMock _context;
    private readonly Mock<IWriteOnlyTransaction> _transactionMock
        = new();

    public SucceededStateHandlerFacts()
    {
        _context = new ApplyStateContextMock();
    }

    [Fact]
    public void ShouldWorkOnlyWithSucceededState()
    {
        SucceededState.Handler handler = new();
        Assert.Equal(SucceededState.StateName, handler.StateName);
    }

    [Fact]
    public void Apply_ShouldIncrease_SucceededCounter()
    {
        SucceededState.Handler handler = new();
        handler.Apply(_context.Object, _transactionMock.Object);

        _transactionMock.Verify(x => x.IncrementCounter("stats:succeeded"), Times.Once);
    }

    [Fact]
    public void Unapply_ShouldDecrementStatistics()
    {
        SucceededState.Handler handler = new();
        handler.Unapply(_context.Object, _transactionMock.Object);

        _transactionMock.Verify(x => x.DecrementCounter("stats:succeeded"), Times.Once);
    }
}
