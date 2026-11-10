using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.Core.Tests.Common;

public class ReflectionJobInvokerFacts
{
    [Fact]
    public void Ctor_DescribesTheMethod_AsAReflectionInvoker()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        Assert.Equal(JobInvokerKind.Reflection, invoker.Kind);
        Assert.Equal(typeof(Jobs), invoker.Type);
        Assert.Equal([typeof(string), typeof(int)], invoker.ParameterTypes);
    }

    [Fact]
    public void Invoke_ThrowsAnException_WhenArgumentsAreNull()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => invoker.Invoke(new Jobs(), null));

        Assert.Equal("arguments", exception.ParamName);
    }

    [Fact]
    public void Invoke_CallsInstanceMethods_AndReturnsTheResult()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        object? result = invoker.Invoke(new Jobs(), ["a", 1]);

        Assert.Equal("a1", result);
    }

    [Fact]
    public void Invoke_CallsStaticMethods_AndReturnsNullForVoid()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.StaticVoid)));

        object? result = invoker.Invoke(null, []);

        Assert.Null(result);
    }

    [Fact]
    public void Invoke_ThrowsTheJobsOwnException_InsteadOfTargetInvocationException()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.Throw)));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => invoker.Invoke(new Jobs(), []));

        Assert.Equal("boom", exception.Message);
        Assert.Contains(nameof(Jobs.Throw), exception.StackTrace);
    }

    [Fact]
    public void Invoke_LetsArgumentMismatchesThrough_Unwrapped()
    {
        ReflectionJobInvoker invoker = new(typeof(Jobs).GetMethod(nameof(Jobs.Concat)));

        Assert.Throws<ArgumentException>(() => invoker.Invoke(new Jobs(), [1, "a"]));
    }

    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public class Jobs
    {
        public string Concat(string text, int number) => text + number;

        public static void StaticVoid()
        {
        }

        public void Throw() => throw new InvalidOperationException("boom");
    }
}
