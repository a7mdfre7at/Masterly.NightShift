using System;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class TaskShapeAdapterFacts
    {
        [Fact]
        public void Adapt_ThrowsAnException_WhenSourceIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => TaskShapeAdapter.Adapt(null, typeof(Task)));
            Assert.Equal("source", exception.ParamName);
        }

        [Fact]
        public void Adapt_ThrowsAnException_WhenReturnTypeIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => TaskShapeAdapter.Adapt(Task.FromResult<object>(null), null));
            Assert.Equal("returnType", exception.ParamName);
        }

        [Fact]
        public void Adapt_ReturnsTheValue_ForSynchronousReturnTypes()
        {
            var result = TaskShapeAdapter.Adapt(Task.FromResult<object>(5), typeof(int));

            Assert.Equal(5, result);
        }

        [Fact]
        public void Adapt_ReturnsTheSourceTask_ForTask()
        {
            var source = Task.FromResult<object>("x");

            var result = TaskShapeAdapter.Adapt(source, typeof(Task));

            Assert.Same(source, result);
        }

        [Fact]
        public async Task Adapt_ConvertsToTaskOfTheResultType()
        {
            var result = TaskShapeAdapter.Adapt(Task.FromResult<object>(7), typeof(Task<int>));

            var task = Assert.IsAssignableFrom<Task<int>>(result);
            Assert.Equal(7, await task);
        }

        [Fact]
        public async Task Adapt_WrapsTheSource_InANonGenericValueTask()
        {
            var source = new TaskCompletionSource<object>();

            var result = TaskShapeAdapter.Adapt(source.Task, typeof(ValueTask));

            var valueTask = Assert.IsType<ValueTask>(result);
            Assert.False(valueTask.IsCompleted);
            source.SetResult(null);
            await valueTask;
            Assert.True(valueTask.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task Adapt_WrapsTheConvertedSource_InAGenericValueTask()
        {
            var result = TaskShapeAdapter.Adapt(Task.FromResult<object>("done"), typeof(ValueTask<string>));

            var valueTask = Assert.IsType<ValueTask<string>>(result);
            Assert.Equal("done", await valueTask);
        }

        [Fact]
        public async Task Adapt_PropagatesTheSourceException_ThroughTheNonGenericValueTask()
        {
            var result = TaskShapeAdapter.Adapt(Task.FromException<object>(new InvalidOperationException("boom")), typeof(ValueTask));

            var valueTask = Assert.IsType<ValueTask>(result);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await valueTask);
            Assert.Equal("boom", exception.Message);
        }

        [Fact]
        public void Adapt_ThrowsNotSupported_ForNonGenericTaskSubclasses()
        {
            var exception = Assert.Throws<NotSupportedException>(
                () => TaskShapeAdapter.Adapt(Task.FromResult<object>(null), typeof(CustomTask)));

            Assert.Contains(typeof(CustomTask).ToString(), exception.Message);
        }

        [Fact]
        public void Adapt_ThrowsNotSupported_ForGenericTaskSubclasses()
        {
            var exception = Assert.Throws<NotSupportedException>(
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
}
