using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    /// <summary>
    /// A job created for one overload must run that overload, even when a less specific one (taking a base
    /// class or an interface) is declared before it.
    /// </summary>
    public class JobOverloadFacts
    {
        [DataCompatibilityRangeFact]
        public void InvocationData_RoundTrip_KeepsTheDerivedOverload_WhenTheBaseOneIsDeclaredFirst()
        {
            var job = Job.FromExpression<OverloadedJobs>(x => x.Handle(new DerivedMessage { Extra = 5 }));

            var deserialized = InvocationData.SerializeJob(job).DeserializeJob();

            Assert.Equal(job.Method, deserialized.Method);
            Assert.Equal(5, Assert.IsType<DerivedMessage>(deserialized.Args[0]).Extra);
        }

        [DataCompatibilityRangeFact]
        public void InvocationData_RoundTrip_KeepsTheClassOverload_WhenTheInterfaceOneIsDeclaredFirst()
        {
            var job = Job.FromExpression<OverloadedJobs>(x => x.Process(new OrderMessage { Id = 7 }));

            var deserialized = InvocationData.SerializeJob(job).DeserializeJob();

            Assert.Equal(job.Method, deserialized.Method);
        }

        [DataCompatibilityRangeFact]
        public void InvocationData_RoundTrip_KeepsTheBaseOverload()
        {
            var job = Job.FromExpression<OverloadedJobs>(x => x.Handle(new BaseMessage()));

            var deserialized = InvocationData.SerializeJob(job).DeserializeJob();

            Assert.Equal(typeof(BaseMessage), deserialized.Method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void FromExpression_WithACapturedInstance_KeepsTheOverloadTheExpressionNames()
        {
            var instance = new OverloadedJobs();

            var job = Job.FromExpression(() => instance.Handle(new DerivedMessage()));

            Assert.Equal(typeof(DerivedMessage), job.Method.GetParameters()[0].ParameterType);
        }

        [Fact]
        [SuppressMessage("ReSharper", "PossibleNullReferenceException")]
        public void GeneratedInvoker_ForTheDerivedOverload_ResolvesThatOverload()
        {
            // The lambda makes the source generator emit an invoker for Handle(DerivedMessage)
            var job = Job.FromExpression<OverloadedJobs>(x => x.Handle(new DerivedMessage()));

            Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var invoker));
            Assert.Equal(job.Method, invoker.Method);
            Assert.Equal("derived", invoker.Invoke(new OverloadedJobs(), new object[] { new DerivedMessage() }));
        }

        public class BaseMessage
        {
        }

        public class DerivedMessage : BaseMessage
        {
            public int Extra { get; set; }
        }

        public interface IMessage
        {
            int Id { get; }
        }

        public class OrderMessage : IMessage
        {
            public int Id { get; set; }
        }

        public class OverloadedJobs
        {
            public string Handle(BaseMessage message) => "base";

            public string Handle(DerivedMessage message) => "derived";

            public string Process(IMessage message) => "interface";

            public string Process(OrderMessage message) => "class";
        }
    }
}
