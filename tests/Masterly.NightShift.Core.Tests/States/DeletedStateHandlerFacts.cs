namespace Masterly.NightShift.Core.Tests.States;

public class DeletedStateHandlerFacts
{
    private readonly ApplyStateContextMock _context;
    private readonly Mock<IWriteOnlyTransaction> _transactionMock
        = new();

    public DeletedStateHandlerFacts()
    {
        _context = new ApplyStateContextMock();
    }

    [Fact]
    public void ShouldWorkOnlyWithDeletedState()
    {
        DeletedState.Handler handler = new();
        Assert.Equal(DeletedState.StateName, handler.StateName);
    }

    [Fact]
    public void Apply_ShouldIncrease_DeletedCounter()
    {
        DeletedState.Handler handler = new();
        handler.Apply(_context.Object, _transactionMock.Object);

        _transactionMock.Verify(x => x.IncrementCounter("stats:deleted"), Times.Once);
    }

    [Fact]
    public void Unapply_ShouldDecrementStatistics()
    {
        DeletedState.Handler handler = new();
        handler.Unapply(_context.Object, _transactionMock.Object);

        _transactionMock.Verify(x => x.DecrementCounter("stats:deleted"), Times.Once);
    }
}
