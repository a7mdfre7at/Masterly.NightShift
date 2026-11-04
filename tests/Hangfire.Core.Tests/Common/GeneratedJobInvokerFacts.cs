using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Server;
using Xunit;

namespace Hangfire.Core.Tests.Common
{
    /// <summary>
    /// The test project references Hangfire.SourceGenerator as an analyzer, so the methods used in the
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

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static int StaticJob(int value) => value * 2;

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
        }

        [GenerateJobInvokers]
        public class MarkedJobs
        {
            public string NeverReferenced() => "never";
        }
    }
}
