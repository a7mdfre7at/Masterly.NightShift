using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Hangfire.Common;
using Xunit;

namespace Hangfire.Core.Tests.Common
{
    public class CompiledJobInvokerFacts
    {
        [Fact]
        public void TryCreate_ThrowsAnException_WhenMethodIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => CompiledJobInvoker.TryCreate(null));
            Assert.Equal("method", exception.ParamName);
        }

        [Fact]
        public void Invoke_CallsStaticMethods_AndReturnsNullForVoid()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(CompiledJobInvokerFacts).GetMethod(nameof(StaticVoid)));
            _staticVoidCalls = 0;

            var result = invoker.Invoke(null, new object[0]);

            Assert.Null(result);
            Assert.Equal(1, _staticVoidCalls);
            Assert.Equal(JobInvokerKind.Compiled, invoker.Kind);
        }

        [Fact]
        public void Invoke_CallsInstanceMethods_WithConvertedArguments()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            var result = invoker.Invoke(new Jobs(), new object[] { "a", 5, 2.5 });

            Assert.Equal("a-5-2.5", result);
        }

        [Fact]
        public void Invoke_PassesDefaultValue_WhenArgumentForValueTypeIsNull()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

            var result = invoker.Invoke(new Jobs(), new object[] { null, null, null });

            Assert.Equal("-0-0", result);
        }

        [Fact]
        public void Invoke_PassesNull_ForNullableValueTypes()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Nullable)));

            Assert.Equal("none", invoker.Invoke(new Jobs(), new object[] { null }));
            Assert.Equal("7", invoker.Invoke(new Jobs(), new object[] { 7 }));
        }

        [Fact]
        public async Task Invoke_ReturnsTheTask_ForAsyncMethods()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Async)));

            var result = invoker.Invoke(new Jobs(), new object[] { 3 });

            var task = Assert.IsAssignableFrom<Task<int>>(result);
            Assert.Equal(6, await task);
        }

        [Fact]
        public void Invoke_ThrowsTheOriginalException_WithoutWrapping()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Throw)));

            var exception = Assert.Throws<InvalidOperationException>(() => invoker.Invoke(new Jobs(), new object[0]));
            Assert.Equal("boom", exception.Message);
        }

        [Fact]
        public void Invoke_WorksForInterfaceMethods_OnImplementingInstances()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(IJobs).GetMethod(nameof(IJobs.Describe)));

            Assert.Equal("jobs", invoker.Invoke(new Jobs(), new object[0]));
        }

        [Fact]
        public void Invoke_WorksForMethodsOnGenericTypes()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(GenericJobs<int>).GetMethod(nameof(GenericJobs<int>.Echo)));

            Assert.Equal(12, invoker.Invoke(new GenericJobs<int>(), new object[] { 12 }));
        }

        [Fact]
        public void Invoke_WorksForConstructedGenericMethods()
        {
            var method = typeof(Jobs).GetMethod(nameof(Jobs.Generic)).MakeGenericMethod(typeof(string));
            var invoker = CompiledJobInvoker.TryCreate(method);

            Assert.Equal("String:x", invoker.Invoke(new Jobs(), new object[] { "x" }));
        }

        [Fact]
        public void TryCreate_ReturnsNull_ForOpenGenericMethods()
        {
            var invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Generic)));

            Assert.Null(invoker);
        }

        private static int _staticVoidCalls;

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void StaticVoid()
        {
            _staticVoidCalls++;
        }

        public interface IJobs
        {
            string Describe();
        }

        public class Jobs : IJobs
        {
            public string Concat(string text, int number, double value) => $"{text}-{number}-{value}";
            public string Nullable(int? value) => value?.ToString() ?? "none";
            public async Task<int> Async(int value) { await Task.Yield(); return value * 2; }
            public void Throw() => throw new InvalidOperationException("boom");
            public string Describe() => "jobs";
            public string Generic<T>(T value) => $"{typeof(T).Name}:{value}";
        }

        public class GenericJobs<T>
        {
            public T Echo(T value) => value;
        }
    }
}
