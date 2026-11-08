using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    /// <summary>
    /// The test project references Masterly.NightShift.SourceGenerator as an analyzer, so the methods used in the
    /// job lambdas below get generated invokers at build time. These facts check the whole path: discovery,
    /// generated code, module-initializer registration and resolution by the performer.
    /// </summary>
    public class GeneratedJobInvokerFacts
    {
        [Fact]
        public void GeneratedInvoker_IsRegistered_ForMethodsUsedInEnqueueLambdas()
        {
            // The lambda below is what makes the generator emit an invoker for `Jobs.Plain`
            var job = Job.FromExpression<Jobs>(x => x.Plain("a", 1));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal(JobInvokerKind.Generated, invoker.Kind);
            Assert.Equal(typeof(Jobs), invoker.Type);
            Assert.Equal("a1", invoker.Invoke(new Jobs(), new object[] { "a", 1 }));
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForStaticMethods_UsedInStaticLambdas()
        {
            var job = Job.FromExpression(() => StaticJob(5));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal(10, invoker.Invoke(null, new object[] { 5 }));
        }

        [Fact]
        public async Task GeneratedInvoker_IsRegistered_ForAsyncMethods()
        {
            var job = Job.FromExpression<Jobs>(x => x.AsyncJob(2));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));

            var task = Assert.IsAssignableFrom<Task<int>>(invoker.Invoke(new Jobs(), new object[] { 2 }));
            Assert.Equal(4, await task);
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForInterfaceMethods()
        {
            var job = Job.FromExpression<IJobs>(x => x.Describe());

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal(typeof(IJobs), invoker.Type);
            Assert.Equal("jobs", invoker.Invoke(new Jobs(), new object[0]));
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForConstructedGenericMethods()
        {
            var job = Job.FromExpression<Jobs>(x => x.Generic<DateTime>(default));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal(new[] { typeof(DateTime) }, invoker.GenericArguments);
            Assert.Equal("DateTime", invoker.Invoke(new Jobs(), new object[] { null }));
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForEveryPublicMethod_OfMarkedTypes()
        {
            // No lambda refers to this method anywhere: the [GenerateJobInvokers] attribute is the only reason
            var method = typeof(MarkedJobs).GetMethod(nameof(MarkedJobs.NeverReferenced));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(method, out var invoker));
            Assert.Equal("never", invoker.Invoke(new MarkedJobs(), new object[0]));
        }

        [Fact]
        public void GeneratedInvoker_PassesDefaultValue_ForNullValueTypeArguments()
        {
            var job = Job.FromExpression<Jobs>(x => x.Plain("a", 1));
            JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker);

            Assert.Equal("0", invoker.Invoke(new Jobs(), new object[] { null, null }));
        }

        [Fact]
        public void GeneratedInvoker_ThrowsTheOriginalException()
        {
            var job = Job.FromExpression<Jobs>(x => x.Throw());
            JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker);

            Assert.Throws<InvalidOperationException>(() => invoker.Invoke(new Jobs(), new object[0]));
        }

        [Fact, StaticLock]
        public void Performer_UsesTheGeneratedInvoker()
        {
            var context = new PerformContextMock();
            context.BackgroundJob.Job = Job.FromExpression<Jobs>(x => x.Plain("x", 7));
            var performer = new CoreBackgroundJobPerformer(new JobActivator(), null);
            JobInvokerRegistry.ClearResolved();

            var result = performer.Perform(context.Object);

            Assert.Equal("x7", result);
            Assert.Equal(JobInvokerKind.Generated, JobInvokerRegistry.GetInvoker(context.BackgroundJob.Job.Method).Kind);
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForAMethodNamedLikeAKeyword()
        {
            // Before the fix, the generated call `x.event(1)` didn't compile
            var job = Job.FromExpression<KeywordJobs>(x => x.@event(1));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal("event 1", invoker.Invoke(new KeywordJobs(), new object[] { 1 }));
        }

        [Fact]
        public void GeneratedInvoker_IsRegistered_ForTheMethodsOfAMarkedRecord_ButNotForItsCompilerGeneratedOnes()
        {
            // Before the fix, the generator emitted a call to `<Clone>$()`, which didn't compile
            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(MarkedRecord).GetMethod(nameof(MarkedRecord.Build)), out var invoker));
            Assert.Equal("built 3", invoker.Invoke(new MarkedRecord(), new object[] { 3 }));

            foreach (var method in typeof(MarkedRecord).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.Name == nameof(MarkedRecord.Build) || method.IsSpecialName) continue;
                Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(method, out _), $"{method} shouldn't have a generated invoker.");
            }
        }

        [Fact]
        public void GeneratedInvoker_IsNotRegistered_ForObjectOverridesOfAMarkedType()
        {
            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(MarkedWithOverrides).GetMethod(nameof(MarkedWithOverrides.Run)), out _));
            Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(MarkedWithOverrides).GetMethod(nameof(ToString), Type.EmptyTypes), out _));
            Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(MarkedWithOverrides).GetMethod(nameof(Equals), new[] { typeof(object) }), out _));
            Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(MarkedWithOverrides).GetMethod(nameof(GetHashCode), Type.EmptyTypes), out _));
        }

#if NET8_0_OR_GREATER // .NET Framework doesn't support static abstract interface members
        [Fact]
        public void GeneratedInvoker_IsRegistered_ForTheInstanceMembersOfAMarkedInterface_WithStaticAbstractMembers()
        {
            // Before the fix, the generated call of the static abstract member didn't compile (CS8926)
            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(IKindJobs).GetMethod(nameof(IKindJobs.Run)), out var invoker));
            Assert.Equal("run", invoker.Invoke(new KindJobs(), new object[0]));
            Assert.False(JobInvokerRegistry.TryGetGeneratedInvoker(typeof(IKindJobs).GetMethod(nameof(IKindJobs.Kind)), out _));
        }
#endif

        [Fact]
        public void GeneratedInvoker_UnboxesAnUnderlyingValue_ForAnEnumParameter_LikeTheCompiledInvoker()
        {
            var job = Job.FromExpression<Jobs>(x => x.Echo(Color.Red));
            JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var generated);
            var compiled = CompiledJobInvoker.TryCreate(job.Method);

            Assert.Equal("Green", generated.Invoke(new Jobs(), new object[] { 2 }));
            Assert.Equal("Green", compiled.Invoke(new Jobs(), new object[] { 2 }));
        }

        [Fact]
        public void GeneratedInvoker_Throws_ForAValueOfAnotherType_LikeTheCompiledInvoker()
        {
            var job = Job.FromExpression<Jobs>(x => x.Plain("a", 1));
            JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var generated);
            var compiled = CompiledJobInvoker.TryCreate(job.Method);

            // Before the fix, the generated invoker passed 0 instead
            Assert.Throws<InvalidCastException>(() => generated.Invoke(new Jobs(), new object[] { "a", 5L }));
            Assert.Throws<InvalidCastException>(() => compiled.Invoke(new Jobs(), new object[] { "a", 5L }));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static int StaticJob(int value) => value * 2;

        public enum Color
        {
            Red = 1,
            Green = 2
        }

        public interface IJobs
        {
            string Describe();
        }

        public class Jobs : IJobs
        {
            public string Plain(string text, int number) => text + number;
            public async Task<int> AsyncJob(int value) { await Task.Yield(); return value * 2; }
            public string Describe() => "jobs";
            public string Generic<T>(T value) => typeof(T).Name;
            public void Throw() => throw new InvalidOperationException();
            public string Echo(Color color) => color.ToString();
        }

        [GenerateJobInvokers]
        public class MarkedJobs
        {
            public string NeverReferenced() => "never";
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public class KeywordJobs
        {
            public string @event(int id) => "event " + id;
        }

        [GenerateJobInvokers]
        public record MarkedRecord
        {
            public string Build(int id) => "built " + id;
        }

        [GenerateJobInvokers]
        public class MarkedWithOverrides
        {
            public string Run() => "run";
            public override string ToString() => "marked";
            public override bool Equals(object obj) => ReferenceEquals(this, obj);
            public override int GetHashCode() => 1;
        }

#if NET8_0_OR_GREATER
        [GenerateJobInvokers]
        public interface IKindJobs
        {
            static abstract string Kind();
            string Run();
        }

        public class KindJobs : IKindJobs
        {
            public static string Kind() => "kind";
            public string Run() => "run";
        }
#endif
    }
}
