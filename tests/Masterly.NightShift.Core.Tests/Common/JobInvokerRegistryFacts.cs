using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;

namespace Masterly.NightShift.Core.Tests.Common;

public class JobInvokerRegistryFacts
{
    [Fact]
    public void Register_ThrowsAnException_WhenInvokerIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.Register(null));
        Assert.Equal("invoker", exception.ParamName);
    }

    [Fact]
    public void GetInvoker_ThrowsAnException_WhenMethodIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.GetInvoker(null));
        Assert.Equal("method", exception.ParamName);
    }

    [Fact, StaticLock]
    public void GetInvoker_DoesNotCountAReflectionInvoker_ForGlobalMethods()
    {
        DynamicMethod method = new("Global", typeof(void), Type.EmptyTypes);
        JobInvokerRegistry.ClearResolved();

        Assert.Throws<ArgumentException>(() => JobInvokerRegistry.GetInvoker(method));

        Assert.Equal(0, JobInvokerRegistry.GetStatistics().Reflection);
    }

    [Fact, StaticLock]
    public void GetInvoker_ReturnsRegisteredGeneratedInvoker_ForTheSameSignature()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(RegistryOnlyMethod));
        FakeGeneratedInvoker invoker = new(typeof(JobInvokerRegistryFacts), nameof(RegistryOnlyMethod), [typeof(string)]);

        JobInvokerRegistry.Register(invoker);
        JobInvokerRegistry.ClearResolved();

        JobInvoker resolved = JobInvokerRegistry.GetInvoker(method);

        Assert.Same(invoker, resolved);
        Assert.Equal(JobInvokerKind.Generated, resolved.Kind);
    }

    [Fact, StaticLock]
    public void GetInvoker_ReturnsTheSameInstance_ForTheSameMethod()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
        JobInvokerRegistry.ClearResolved();

        JobInvoker first = JobInvokerRegistry.GetInvoker(method);
        JobInvoker second = JobInvokerRegistry.GetInvoker(method);

        Assert.Same(first, second);
    }

    [Fact, StaticLock]
    public void GetInvoker_ReturnsCompiledInvoker_WhenNothingIsRegistered_AndCompilationIsEnabled()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
        JobInvokerRegistry.ClearResolved();
        JobInvokerRegistry.UseCompiledInvokers = true;

        JobInvoker invoker = JobInvokerRegistry.GetInvoker(method);

        Assert.Equal(JobInvokerKind.Compiled, invoker.Kind);
        Assert.Equal(1, JobInvokerRegistry.GetStatistics().Compiled);
    }

    [Fact, StaticLock]
    public void GetInvoker_ReturnsReflectionInvoker_WhenCompilationIsDisabled()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
        JobInvokerRegistry.ClearResolved();

        try
        {
            JobInvokerRegistry.UseCompiledInvokers = false;

            JobInvoker invoker = JobInvokerRegistry.GetInvoker(method);

            Assert.Equal(JobInvokerKind.Reflection, invoker.Kind);
            Assert.Equal(1, JobInvokerRegistry.GetStatistics().Reflection);
        }
        finally
        {
            JobInvokerRegistry.UseCompiledInvokers = true;
            JobInvokerRegistry.ClearResolved();
        }
    }

    [Fact, StaticLock]
    public void GetInvoker_UsesTheDeclaringType_SoDerivedInstancesShareTheInvoker()
    {
        MethodInfo? baseMethod = typeof(BaseJobs).GetMethod(nameof(BaseJobs.Run));
        MethodInfo? derivedMethod = typeof(DerivedJobs).GetMethod(nameof(DerivedJobs.Run));
        JobInvokerRegistry.ClearResolved();

        JobInvoker fromBase = JobInvokerRegistry.GetInvoker(baseMethod);
        JobInvoker fromDerived = JobInvokerRegistry.GetInvoker(derivedMethod);

        // Different ReflectedType, same declaring method: the compiled delegate is built for the declaring type
        Assert.Equal(typeof(BaseJobs), fromBase.Type);
        Assert.Equal(typeof(BaseJobs), fromDerived.Type);

        Assert.Same(fromBase, fromDerived);
        Assert.Equal("base:42", fromBase.Invoke(new DerivedJobs(), [42]));
    }

    [Fact, StaticLock]
    public void TryGetGeneratedInvoker_ReturnsFalse_ForMethodsWithoutGeneratedInvoker()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));

        Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(method, out JobInvoker? invoker));
        Assert.Null(invoker);
    }

    [Fact, StaticLock]
    public void GetStatistics_CountsRegisteredGeneratedInvokers()
    {
        JobInvokerRegistry.Register(new FakeGeneratedInvoker(typeof(JobInvokerRegistryFacts), nameof(RegistryOnlyMethod), [typeof(string)]));

        Assert.True(JobInvokerRegistry.GetStatistics().Registered >= 1);
    }

    [Fact]
    public void RegisterAssembly_ThrowsAnException_WhenAssemblyIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.RegisterAssembly(null));
        Assert.Equal("assembly", exception.ParamName);
    }

    [Fact]
    public void RegisterAssembly_DoesNotThrow_ForAssembliesWithoutProviders()
    {
        JobInvokerRegistry.RegisterAssembly(typeof(string).GetTypeInfo().Assembly);
    }

    [Fact]
    public void TryGetGeneratedInvoker_ThrowsAnException_WhenMethodIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.TryGetGeneratedInvoker(null, out _));
        Assert.Equal("method", exception.ParamName);
    }

    [Fact, StaticLock]
    public void TryGetGeneratedInvoker_ReturnsFalse_ForGlobalMethods()
    {
        DynamicMethod method = new("Global", typeof(void), Type.EmptyTypes);

        bool found = JobInvokerRegistry.TryGetGeneratedInvoker(method, out JobInvoker? invoker);

        Assert.False(found);
        Assert.Null(invoker);
    }

    [Fact, StaticLock]
    public void GetInvoker_ThrowsAnException_ForGlobalMethods()
    {
        DynamicMethod method = new("Global", typeof(void), Type.EmptyTypes);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => JobInvokerRegistry.GetInvoker(method));

        Assert.Equal("method", exception.ParamName);
    }

    [Fact, StaticLock]
    public void GetInvoker_FallsBackToReflection_WhenTheMethodCannotBeCompiled()
    {
        MethodInfo? method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(ByRefMethod));
        object[] arguments = [41];
        JobInvokerRegistry.ClearResolved();
        JobInvokerRegistry.UseCompiledInvokers = true;

        JobInvoker invoker = JobInvokerRegistry.GetInvoker(method);
        invoker.Invoke(null, arguments);

        Assert.Equal(JobInvokerKind.Reflection, invoker.Kind);
        Assert.Equal(1, JobInvokerRegistry.GetStatistics().Reflection);
        Assert.Equal(0, JobInvokerRegistry.GetStatistics().Compiled);
        Assert.Equal(42, arguments[0]);
        JobInvokerRegistry.ClearResolved();
    }

    [Fact, StaticLock]
    public void RegisterAssembly_DoesNotThrow_ForDynamicAssemblies()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Dynamic{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);

        JobInvokerRegistry.RegisterAssembly(assembly);
        JobInvokerRegistry.RegisterAssembly(assembly);
    }

#if NET9_0_OR_GREATER
    [Fact, StaticLock]
    public void RegisterAssembly_SkipsProvidersWithoutAParameterlessStaticRegisterMethod_AndRunsTheOthers()
    {
        CountingProvider.Calls = 0;
        Assembly assembly = EmitAssemblyWithProviders(typeof(ProviderWithoutRegister), typeof(ProviderWithRegisterParameters), typeof(CountingProvider));

        JobInvokerRegistry.RegisterAssembly(assembly);

        Assert.Equal(1, CountingProvider.Calls);
    }

    [Fact, StaticLock]
    public void RegisterAssembly_RunsProvidersOnlyOnce_PerAssembly()
    {
        CountingProvider.Calls = 0;
        Assembly assembly = EmitAssemblyWithProviders(typeof(CountingProvider));

        JobInvokerRegistry.RegisterAssembly(assembly);
        JobInvokerRegistry.RegisterAssembly(assembly);

        Assert.Equal(1, CountingProvider.Calls);
    }

    [Fact, StaticLock]
    public void RegisterAssembly_SwallowsExceptions_ThrownByAProvider()
    {
        ThrowingProvider.Calls = 0;
        Assembly assembly = EmitAssemblyWithProviders(typeof(ThrowingProvider));

        JobInvokerRegistry.RegisterAssembly(assembly);

        Assert.Equal(1, ThrowingProvider.Calls);
    }

    private static Assembly EmitAssemblyWithProviders(params Type[] providerTypes)
    {
        ConstructorInfo? constructor = typeof(JobInvokersProviderAttribute).GetConstructor([typeof(Type)]);
        PersistedAssemblyBuilder builder = new(new AssemblyName($"Providers{Guid.NewGuid():N}"), typeof(object).Assembly);
        builder.DefineDynamicModule("Providers");

        foreach (Type providerType in providerTypes)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(constructor, [providerType]));
        }

        using MemoryStream stream = new();
        builder.Save(stream);
        return Assembly.Load(stream.ToArray());
    }
#endif

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void ByRefMethod(ref int value)
    {
        value++;
    }

    public static class CountingProvider
    {
        public static int Calls;

        public static void Register() => Calls++;
    }

    public static class ThrowingProvider
    {
        public static int Calls;

        public static void Register()
        {
            Calls++;
            throw new InvalidOperationException("Broken provider.");
        }
    }

    public static class ProviderWithoutRegister
    {
    }

    public static class ProviderWithRegisterParameters
    {
        [SuppressMessage("ReSharper", "UnusedParameter.Global")]
        public static void Register(int value)
        {
        }
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static string RegistryOnlyMethod(string value) => value;

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void NotGeneratedMethod()
    {
    }

    public class BaseJobs
    {
        public string Run(int value) => $"base:{value}";
    }

    public class DerivedJobs : BaseJobs
    {
    }

    private sealed class FakeGeneratedInvoker(Type type, string methodName, Type[] parameterTypes) : JobInvoker(type, methodName, parameterTypes, Type.EmptyTypes)
    {
        public override object Invoke(object instance, object[] arguments) => arguments[0];
    }
}

public class JobInvokerFacts
{
    [Fact]
    public void Ctor_ThrowsAnException_WhenTypeIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new TestInvoker(null, "Method", Type.EmptyTypes, Type.EmptyTypes));
        Assert.Equal("type", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMethodNameIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new TestInvoker(typeof(JobInvokerFacts), null, Type.EmptyTypes, Type.EmptyTypes));
        Assert.Equal("methodName", exception.ParamName);
    }

    [Fact]
    public void Method_IsResolvedLazily_FromTheSignature()
    {
        TestInvoker invoker = new(typeof(JobInvokerFacts), nameof(Sample), [typeof(int), typeof(string)], Type.EmptyTypes);

        MethodInfo method = invoker.Method;

        Assert.Equal(typeof(JobInvokerFacts).GetMethod(nameof(Sample)), method);
        Assert.Equal(typeof(Task<string>), invoker.ReturnType);
        Assert.True(invoker.IsStatic);
        Assert.Equal(JobInvokerKind.Generated, invoker.Kind);
    }

    [Fact]
    public void Method_IsResolved_ForConstructedGenericMethods()
    {
        TestInvoker invoker = new(typeof(JobInvokerFacts), nameof(Generic), [typeof(int)], [typeof(int)]);

        MethodInfo method = invoker.Method;

        Assert.True(method.IsGenericMethod);
        Assert.False(method.IsGenericMethodDefinition);
        Assert.Equal(typeof(int), method.GetGenericArguments()[0]);
    }

    [Fact]
    public void Method_IsClosedOverTheGenericArguments_WhenTheSignatureUsesTheOpenGenericParameters()
    {
        Type openParameter = typeof(JobInvokerFacts).GetMethod(nameof(Generic)).GetGenericArguments()[0];
        TestInvoker invoker = new(typeof(JobInvokerFacts), nameof(Generic), [openParameter], [typeof(long)]);

        MethodInfo method = invoker.Method;

        Assert.False(method.IsGenericMethodDefinition);
        Assert.Equal([typeof(long)], method.GetGenericArguments());
    }

    [Fact]
    public void Method_ThrowsAnException_WhenTheSignatureDoesNotExist()
    {
        TestInvoker invoker = new(typeof(JobInvokerFacts), "Missing", Type.EmptyTypes, Type.EmptyTypes);

        Assert.Throws<InvalidOperationException>(() => invoker.Method);
    }

    [Fact]
    public void Method_ThrowsAnException_NamingTheParameterTypes_WhenTheSignatureDoesNotExist()
    {
        TestInvoker invoker = new(typeof(JobInvokerFacts), nameof(Sample), [typeof(Guid)], Type.EmptyTypes);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => invoker.Method);

        Assert.Contains("Sample(Guid)", exception.Message);
        Assert.Contains(typeof(JobInvokerFacts).FullName, exception.Message);
    }

    [Fact]
    public void ToString_DescribesTheInvoker()
    {
        TestInvoker invoker = new(typeof(JobInvokerFacts), nameof(Sample), [typeof(int), typeof(string)], Type.EmptyTypes);

        Assert.Equal("Generated: JobInvokerFacts.Sample(Int32, String)", invoker.ToString());
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static Task<string> Sample(int number, string text) => Task.FromResult(text + number);

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Generic<T>(T value)
    {
    }

    private sealed class TestInvoker(Type type, string methodName, Type[] parameterTypes, Type[] genericArguments) : JobInvoker(type, methodName, parameterTypes, genericArguments)
    {
        public override object Invoke(object instance, object[] arguments) => throw new NotSupportedException();
    }
}
