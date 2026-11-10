using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Masterly.NightShift.Core.Tests.Common;

public class CompiledJobInvokerFacts
{
    [Fact]
    public void TryCreate_ThrowsAnException_WhenMethodIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => CompiledJobInvoker.TryCreate(null));
        Assert.Equal("method", exception.ParamName);
    }

    [Fact]
    public void Invoke_CallsStaticMethods_AndReturnsNullForVoid()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(CompiledJobInvokerFacts).GetMethod(nameof(StaticVoid)));
        _staticVoidCalls = 0;

        object? result = invoker.Invoke(null, []);

        Assert.Null(result);
        Assert.Equal(1, _staticVoidCalls);
        Assert.Equal(JobInvokerKind.Compiled, invoker.Kind);
    }

    [Fact]
    public void Invoke_CallsInstanceMethods_WithConvertedArguments()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        object? result = invoker.Invoke(new Jobs(), ["a", 5, 2.5]);

        Assert.Equal("a-5-2.5", result);
    }

    [Fact]
    public void Invoke_PassesDefaultValue_WhenArgumentForValueTypeIsNull()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        object? result = invoker.Invoke(new Jobs(), [null, null, null]);

        Assert.Equal("-0-0", result);
    }

    [Fact]
    public void Invoke_PassesNull_ForNullableValueTypes()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Nullable)));

        Assert.Equal("none", invoker.Invoke(new Jobs(), [null]));
        Assert.Equal("7", invoker.Invoke(new Jobs(), [7]));
    }

    [Fact]
    public async Task Invoke_ReturnsTheTask_ForAsyncMethods()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Async)));

        object? result = invoker.Invoke(new Jobs(), [3]);

        Task<int> task = Assert.IsAssignableFrom<Task<int>>(result);
        Assert.Equal(6, await task);
    }

    [Fact]
    public void Invoke_ThrowsTheOriginalException_WithoutWrapping()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Throw)));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => invoker.Invoke(new Jobs(), []));
        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public void Invoke_WorksForInterfaceMethods_OnImplementingInstances()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(IJobs).GetMethod(nameof(IJobs.Describe)));

        Assert.Equal("jobs", invoker.Invoke(new Jobs(), []));
    }

    [Fact]
    public void Invoke_WorksForMethodsOnGenericTypes()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(GenericJobs<int>).GetMethod(nameof(GenericJobs<>.Echo)));

        Assert.Equal(12, invoker.Invoke(new GenericJobs<int>(), [12]));
    }

    [Fact]
    public void Invoke_WorksForConstructedGenericMethods()
    {
        MethodInfo method = typeof(Jobs).GetMethod(nameof(Jobs.Generic)).MakeGenericMethod(typeof(string));
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(method);

        Assert.Equal("String:x", invoker.Invoke(new Jobs(), ["x"]));
    }

    [Fact]
    public void TryCreate_ReturnsNull_ForOpenGenericMethods()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Generic)));

        Assert.Null(invoker);
    }

    [Fact]
    public void TryCreate_ReturnsNull_ForParametersPassedByReference()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.Increment)));

        Assert.Null(invoker);
    }

    [Fact]
    public void TryCreate_ReturnsNull_ForOutParameters()
    {
        CompiledJobInvoker? invoker = CompiledJobInvoker.TryCreate(typeof(Jobs).GetMethod(nameof(Jobs.TryGet)));

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
        public void Increment(ref int value) => value++;
        public bool TryGet(out string value) { value = "x"; return true; }
    }

    public class GenericJobs<T>
    {
        public T Echo(T value) => value;
    }
}
