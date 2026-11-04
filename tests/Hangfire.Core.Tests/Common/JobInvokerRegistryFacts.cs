using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire.Common;
using Xunit;

namespace Hangfire.Core.Tests.Common
{
    public class JobInvokerRegistryFacts
    {
        [Fact]
        public void Register_ThrowsAnException_WhenInvokerIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.Register(null));
            Assert.Equal("invoker", exception.ParamName);
        }

        [Fact]
        public void GetInvoker_ThrowsAnException_WhenMethodIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.GetInvoker(null));
            Assert.Equal("method", exception.ParamName);
        }

        [Fact, StaticLock]
        public void GetInvoker_ReturnsRegisteredGeneratedInvoker_ForTheSameSignature()
        {
            var method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(RegistryOnlyMethod));
            var invoker = new FakeGeneratedInvoker(typeof(JobInvokerRegistryFacts), nameof(RegistryOnlyMethod), new[] { typeof(string) });

            JobInvokerRegistry.Register(invoker);
            JobInvokerRegistry.ClearResolved();

            var resolved = JobInvokerRegistry.GetInvoker(method);

            Assert.Same(invoker, resolved);
            Assert.Equal(JobInvokerKind.Generated, resolved.Kind);
        }

        [Fact, StaticLock]
        public void GetInvoker_ReturnsTheSameInstance_ForTheSameMethod()
        {
            var method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
            JobInvokerRegistry.ClearResolved();

            var first = JobInvokerRegistry.GetInvoker(method);
            var second = JobInvokerRegistry.GetInvoker(method);

            Assert.Same(first, second);
        }

        [Fact, StaticLock]
        public void GetInvoker_ReturnsCompiledInvoker_WhenNothingIsRegistered_AndCompilationIsEnabled()
        {
            var method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
            JobInvokerRegistry.ClearResolved();
            JobInvokerRegistry.UseCompiledInvokers = true;

            var invoker = JobInvokerRegistry.GetInvoker(method);

            Assert.Equal(JobInvokerKind.Compiled, invoker.Kind);
            Assert.Equal(1, JobInvokerRegistry.GetStatistics().Compiled);
        }

        [Fact, StaticLock]
        public void GetInvoker_ReturnsReflectionInvoker_WhenCompilationIsDisabled()
        {
            var method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));
            JobInvokerRegistry.ClearResolved();

            try
            {
                JobInvokerRegistry.UseCompiledInvokers = false;

                var invoker = JobInvokerRegistry.GetInvoker(method);

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
            var baseMethod = typeof(BaseJobs).GetMethod(nameof(BaseJobs.Run));
            var derivedMethod = typeof(DerivedJobs).GetMethod(nameof(DerivedJobs.Run));
            JobInvokerRegistry.ClearResolved();

            var fromBase = JobInvokerRegistry.GetInvoker(baseMethod);
            var fromDerived = JobInvokerRegistry.GetInvoker(derivedMethod);

            // Different ReflectedType, same declaring method: the compiled delegate is built for the declaring type
            Assert.Equal(typeof(BaseJobs), fromBase.Type);
            Assert.Equal(typeof(BaseJobs), fromDerived.Type);

            Assert.Same(fromBase, fromDerived);
            Assert.Equal("base:42", fromBase.Invoke(new DerivedJobs(), new object[] { 42 }));
        }

        [Fact, StaticLock]
        public void TryGetGeneratedInvoker_ReturnsFalse_ForMethodsWithoutGeneratedInvoker()
        {
            var method = typeof(JobInvokerRegistryFacts).GetMethod(nameof(NotGeneratedMethod));

            Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(method, out var invoker));
            Assert.Null(invoker);
        }

        [Fact, StaticLock]
        public void GetStatistics_CountsRegisteredGeneratedInvokers()
        {
            JobInvokerRegistry.Register(new FakeGeneratedInvoker(typeof(JobInvokerRegistryFacts), nameof(RegistryOnlyMethod), new[] { typeof(string) }));

            Assert.True(JobInvokerRegistry.GetStatistics().Registered >= 1);
        }

        [Fact]
        public void RegisterAssembly_ThrowsAnException_WhenAssemblyIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => JobInvokerRegistry.RegisterAssembly(null));
            Assert.Equal("assembly", exception.ParamName);
        }

        [Fact]
        public void RegisterAssembly_DoesNotThrow_ForAssembliesWithoutProviders()
        {
            JobInvokerRegistry.RegisterAssembly(typeof(string).GetTypeInfo().Assembly);
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

        private sealed class FakeGeneratedInvoker : JobInvoker
        {
            public FakeGeneratedInvoker(Type type, string methodName, Type[] parameterTypes)
                : base(type, methodName, parameterTypes, Type.EmptyTypes)
            {
            }

            public override object Invoke(object instance, object[] arguments) => arguments[0];
        }
    }

    public class JobInvokerFacts
    {
        [Fact]
        public void Ctor_ThrowsAnException_WhenTypeIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new TestInvoker(null, "Method", Type.EmptyTypes, Type.EmptyTypes));
            Assert.Equal("type", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenMethodNameIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new TestInvoker(typeof(JobInvokerFacts), null, Type.EmptyTypes, Type.EmptyTypes));
            Assert.Equal("methodName", exception.ParamName);
        }

        [Fact]
        public void Method_IsResolvedLazily_FromTheSignature()
        {
            var invoker = new TestInvoker(typeof(JobInvokerFacts), nameof(Sample), new[] { typeof(int), typeof(string) }, Type.EmptyTypes);

            var method = invoker.Method;

            Assert.Equal(typeof(JobInvokerFacts).GetMethod(nameof(Sample)), method);
            Assert.Equal(typeof(Task<string>), invoker.ReturnType);
            Assert.True(invoker.IsStatic);
            Assert.Equal(JobInvokerKind.Generated, invoker.Kind);
        }

        [Fact]
        public void Method_IsResolved_ForConstructedGenericMethods()
        {
            var invoker = new TestInvoker(typeof(JobInvokerFacts), nameof(Generic), new[] { typeof(int) }, new[] { typeof(int) });

            var method = invoker.Method;

            Assert.True(method.IsGenericMethod);
            Assert.False(method.IsGenericMethodDefinition);
            Assert.Equal(typeof(int), method.GetGenericArguments()[0]);
        }

        [Fact]
        public void Method_ThrowsAnException_WhenTheSignatureDoesNotExist()
        {
            var invoker = new TestInvoker(typeof(JobInvokerFacts), "Missing", Type.EmptyTypes, Type.EmptyTypes);

            Assert.Throws<InvalidOperationException>(() => invoker.Method);
        }

        [Fact]
        public void ToString_DescribesTheInvoker()
        {
            var invoker = new TestInvoker(typeof(JobInvokerFacts), nameof(Sample), new[] { typeof(int), typeof(string) }, Type.EmptyTypes);

            Assert.Equal("Generated: JobInvokerFacts.Sample(Int32, String)", invoker.ToString());
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static Task<string> Sample(int number, string text) => Task.FromResult(text + number);

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Generic<T>(T value)
        {
        }

        private sealed class TestInvoker : JobInvoker
        {
            public TestInvoker(Type type, string methodName, Type[] parameterTypes, Type[] genericArguments)
                : base(type, methodName, parameterTypes, genericArguments)
            {
            }

            public override object Invoke(object instance, object[] arguments) => throw new NotSupportedException();
        }
    }
}
