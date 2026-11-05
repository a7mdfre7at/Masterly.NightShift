using System.Diagnostics;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests.States
{
    public class SucceededStateFacts
    {
        [Fact]
        public void StateName_IsEqualToSucceeded()
        {
            Assert.Equal("Succeeded", SucceededState.StateName);
        }

        [Fact]
        public void NameProperty_ReturnsStateName()
        {
            var state = CreateState();
            Assert.Equal(SucceededState.StateName, state.Name);
        }

        [Fact]
        public void SerializeData_ReturnsCorrectData()
        {
            var state = CreateState();

            var data = state.SerializeData();

            Assert.Equal("\"Returned  value\"", data["Result"]);
            Assert.Equal(JobHelper.SerializeDateTime(state.SucceededAt), data["SucceededAt"]);
            Assert.Equal("123", data["PerformanceDuration"]);
            Assert.Equal("11", data["Latency"]);
        }

        [Fact]
        public void SerializeData_DoesNotAppendEntry_ForNullResult()
        {
            var state = new SucceededState(null, 0, 0);

            var data = state.SerializeData();

            Assert.False(data.ContainsKey("Result"));
        }

        [Fact]
        public void SerializeData_CorrectlyHandlesResults_ThatCantBeSerialized()
        {
            var process = new Process();
            var state = new SucceededState(process, 0, 0);

            var data = state.SerializeData();

            Assert.Contains("Can not serialize", data["Result"]);
        }

        [Fact]
        public void IsFinal_ReturnsTrue()
        {
            var state = CreateState();
            Assert.True(state.IsFinal);
        }

        [Fact]
        public void IgnoreExceptions_ReturnsFalse()
        {
            var state = CreateState();
            Assert.False(state.IgnoreJobLoadException);
        }

        [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
        public void JsonSerialize_ReturnsCorrectString_Before170()
        {
            var state = new SucceededState(null, 1, 2);

            var serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

            Assert.Equal(
                "{\"$type\":\"Masterly.NightShift.States.SucceededState, Masterly.NightShift.Core\",\"Latency\":1,\"PerformanceDuration\":2}",
                serialized);
        }

        [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
        public void JsonSerialize_ReturnsCorrectString_After170()
        {
            var state = new SucceededState(null, 1, 2);

            var serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

            Assert.Equal(
                "{\"$type\":\"Masterly.NightShift.States.SucceededState, Masterly.NightShift.Core\",\"Latency\":1,\"PerformanceDuration\":2}",
                serialized);
        }

        private static SucceededState CreateState()
        {
            return new SucceededState("Returned  value", 11, 123);
        }
    }
}
