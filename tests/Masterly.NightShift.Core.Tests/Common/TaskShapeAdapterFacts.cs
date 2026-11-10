namespace Masterly.NightShift.Core.Tests.Common;

public class TaskShapeAdapterFacts
{
    [Fact]
    public void Adapt_ThrowsAnException_WhenSourceIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => TaskShapeAdapter.Adapt(null, typeof(Task)));
        Assert.Equal("source", exception.ParamName);
    }

    [Fact]
    public void Adapt_ThrowsAnException_WhenReturnTypeIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => TaskShapeAdapter.Adapt(Task.FromResult<object>(null), null));
        Assert.Equal("returnType", exception.ParamName);
    }

    [Fact]
    public void Adapt_ReturnsTheValue_ForSynchronousReturnTypes()
    {
        object? result = TaskShapeAdapter.Adapt(Task.FromResult<object>(5), typeof(int));

        Assert.Equal(5, result);
    }

    [Fact]
    public void Adapt_ReturnsTheSourceTask_ForTask()
    {
        Task<object> source = Task.FromResult<object>("x");

        object? result = TaskShapeAdapter.Adapt(source, typeof(Task));

        Assert.Same(source, result);
    }

    [Fact]
    public async Task Adapt_ConvertsToTaskOfTheResultType()
    {
        object? result = TaskShapeAdapter.Adapt(Task.FromResult<object>(7), typeof(Task<int>));

        Task<int> task = Assert.IsAssignableFrom<Task<int>>(result);
        Assert.Equal(7, await task);
    }

    [Fact]
    public async Task Adapt_WrapsTheSource_InANonGenericValueTask()
    {
        TaskCompletionSource<object> source = new();

        object? result = TaskShapeAdapter.Adapt(source.Task, typeof(ValueTask));

        ValueTask valueTask = Assert.IsType<ValueTask>(result);
        Assert.False(valueTask.IsCompleted);
        source.SetResult(null);
        await valueTask;
        Assert.True(valueTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Adapt_WrapsTheConvertedSource_InAGenericValueTask()
    {
        object? result = TaskShapeAdapter.Adapt(Task.FromResult<object>("done"), typeof(ValueTask<string>));

        ValueTask<string> valueTask = Assert.IsType<ValueTask<string>>(result);
        Assert.Equal("done", await valueTask);
    }

    [Fact]
    public async Task Adapt_PropagatesTheSourceException_ThroughTheNonGenericValueTask()
    {
        object? result = TaskShapeAdapter.Adapt(Task.FromException<object>(new InvalidOperationException("boom")), typeof(ValueTask));

        ValueTask valueTask = Assert.IsType<ValueTask>(result);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await valueTask);
        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public void Adapt_ThrowsNotSupported_ForNonGenericTaskSubclasses()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => TaskShapeAdapter.Adapt(Task.FromResult<object>(null), typeof(CustomTask)));

        Assert.Contains(typeof(CustomTask).ToString(), exception.Message);
    }

    [Fact]
    public void Adapt_ThrowsNotSupported_ForGenericTaskSubclasses()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => TaskShapeAdapter.Adapt(Task.FromResult<object>(null), typeof(CustomTask<int>)));

        Assert.Contains("return type", exception.Message);
    }

    public sealed class CustomTask : Task
    {
        public CustomTask() : base(static () => { })
        {
        }
    }

    public sealed class CustomTask<T> : Task<T>
    {
        public CustomTask() : base(static () => default)
        {
        }
    }
}
