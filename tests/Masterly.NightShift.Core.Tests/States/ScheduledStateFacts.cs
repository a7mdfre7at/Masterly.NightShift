using System.Text.Json;

namespace Masterly.NightShift.Core.Tests.States;

public class ScheduledStateFacts
{
    [Fact]
    public void StateName_IsCorrect()
    {
        ScheduledState state = new(DateTime.UtcNow);
        Assert.Equal(ScheduledState.StateName, state.Name);
    }

    [Fact]
    public void Ctor_SetsTheCorrectData_WhenDateIsPassed()
    {
        DateTime date = new(2012, 12, 12);
        ScheduledState state = new(date);
        Assert.Equal(date, state.EnqueueAt);
    }

    [Fact]
    public void Ctor_SetsTheCorrectDate_WhenTimeSpanIsPassed()
    {
        ScheduledState state = new(TimeSpan.FromDays(1));
        Assert.True(DateTime.UtcNow.AddDays(1).AddMinutes(-1) < state.EnqueueAt);
        Assert.True(state.EnqueueAt < DateTime.UtcNow.AddDays(1).AddMinutes(1));
    }

    [Fact]
    public void SerializeData_ReturnsCorrectData()
    {
        ScheduledState state = new(new DateTime(2012, 12, 12));

        Dictionary<string, string> data = state.SerializeData();

        Assert.Equal(JobHelper.SerializeDateTime(state.EnqueueAt), data["EnqueueAt"]);
        Assert.Equal(JobHelper.SerializeDateTime(state.ScheduledAt), data["ScheduledAt"]);
    }

    [Fact]
    public void SerializeData_DoesNotContainQueueKey_WhenItIsNotSet()
    {
        ScheduledState state = new(TimeSpan.Zero);

        Dictionary<string, string> data = state.SerializeData();

        Assert.DoesNotContain("Queue", data.Keys);
    }

    [Fact]
    public void IsFinal_ReturnsFalse()
    {
        ScheduledState state = new(DateTime.UtcNow);

        Assert.False(state.IsFinal);
    }

    [Fact]
    public void IgnoreExceptions_ReturnsFalse()
    {
        ScheduledState state = new(DateTime.UtcNow);
        Assert.False(state.IgnoreJobLoadException);
    }

    [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_Before170()
    {
        DateTime dateTime = DateTime.UtcNow;
        ScheduledState state = new(dateTime);
        string convertedDateTime = JsonSerializer.Serialize(dateTime);

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.ScheduledState, Masterly.NightShift.Core\",\"EnqueueAt\":" + convertedDateTime + "}",
            serialized);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void JsonSerialize_ReturnsCorrectString_After170()
    {
        DateTime dateTime = DateTime.UtcNow;
        ScheduledState state = new(dateTime);
        string convertedDateTime = JsonSerializer.Serialize(dateTime);

        string serialized = SerializationHelper.Serialize<IState>(state, SerializationOption.TypedInternal);

        Assert.Equal(
            "{\"$type\":\"Masterly.NightShift.States.ScheduledState, Masterly.NightShift.Core\",\"EnqueueAt\":" + convertedDateTime + "}",
            serialized);
    }
}
