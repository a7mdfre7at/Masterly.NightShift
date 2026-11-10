using System.Reflection;
using Masterly.NightShift.Annotations;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Common;

public class JobHelperFacts
{
    private static readonly DateTime WellKnownDateTime = new(1988, 04, 20, 01, 12, 32, DateTimeKind.Utc);
    private const int WellKnownTimestamp = 577501952;
    private const long WellKnownMillisecondTimestamp = 577501952000;

    [DataCompatibilityRangeFact]
    public void ToTimestamp_ReturnsUnixTimestamp_OfTheGivenDateTime()
    {
        long result = JobHelper.ToTimestamp(
            WellKnownDateTime);

        Assert.Equal(WellKnownTimestamp, result);
    }

    [DataCompatibilityRangeFact]
    public void FromTimestamp_ReturnsDateTime_ForGivenTimestamp()
    {
        DateTime result = JobHelper.FromTimestamp(WellKnownTimestamp);

        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact]
    public void ToMillisecondTimestamp_ReturnsTheCorrectResult()
    {
        long result = JobHelper.ToMillisecondTimestamp(WellKnownDateTime);
        Assert.Equal(WellKnownMillisecondTimestamp, result);
    }

    [DataCompatibilityRangeFact]
    public void FromMillisecondTimestamp_ReturnsTheCorrectDateTime()
    {
        DateTime result = JobHelper.FromMillisecondTimestamp(WellKnownMillisecondTimestamp);
        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsString_InISO8601Format_In_Version_Pre_170()
    {
        string result = JobHelper.SerializeDateTime(WellKnownDateTime);

        Assert.Equal(WellKnownDateTime.ToString("O"), result);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsString_WithMillisecondTimestamp_In_Version_170()
    {
        string result = JobHelper.SerializeDateTime(WellKnownDateTime);

        Assert.Equal(WellKnownMillisecondTimestamp.ToString(), result);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsMillisecondTimestamp_ForRecentDates()
    {
        DateTime dateTime = DateTime.UtcNow;
        string result = JobHelper.SerializeDateTime(dateTime);

        Assert.True(long.TryParse(result, out long timestamp));
        Assert.Equal(JobHelper.ToMillisecondTimestamp(dateTime), timestamp);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsMillisecondTimestamp_AtLeastUpTo2100()
    {
        DateTime dateTime = new(2100, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        string result = JobHelper.SerializeDateTime(dateTime);

        Assert.True(long.TryParse(result, out long timestamp));
        Assert.Equal(JobHelper.ToMillisecondTimestamp(dateTime), timestamp);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsISO8601String_ForConflictingRanges_WithSecondBasedTimestamps()
    {
        DateTime dateTime = new(1975, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        string result = JobHelper.SerializeDateTime(dateTime);

        Assert.False(long.TryParse(result, out _));
        Assert.Equal(dateTime.ToString("O"), result);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsISO8601String_WhenTimestampIsNotApplicable()
    {
        DateTime dateTime = new(1900, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        string result = JobHelper.SerializeDateTime(dateTime);

        Assert.False(long.TryParse(result, out _));
        Assert.Equal(dateTime.ToString("O"), result);
    }

    [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170)]
    public void SerializeDateTime_ReturnsISO8601String_WithMaxDateTime()
    {
        DateTime dateTime = DateTime.MaxValue;
        string result = JobHelper.SerializeDateTime(dateTime);

        Assert.False(long.TryParse(result, out _));
        Assert.Equal(dateTime.ToString("O"), result);
    }

    [DataCompatibilityRangeFact]
    public void DeserializeDateTime_CanDeserialize_Timestamps()
    {
        DateTime result = JobHelper.DeserializeDateTime(WellKnownTimestamp.ToString());

        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact]
    public void DeserializeDateTime_CanDeserialize_ISO8601Format()
    {
        DateTime result = JobHelper.DeserializeDateTime(WellKnownDateTime.ToString("o"));
        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact]
    public void DeserializeDateTime_CanDeserialize_MillisecondTimestamp()
    {
        DateTime result = JobHelper.DeserializeDateTime(WellKnownMillisecondTimestamp.ToString());
        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact]
    public void DeserializeNullableDateTime_ReturnsNull_IfNullOrEmptyStringGiven()
    {
        Assert.Null(JobHelper.DeserializeNullableDateTime(""));
        Assert.Null(JobHelper.DeserializeNullableDateTime(null));
    }

    [DataCompatibilityRangeFact]
    public void DeserializeNullableDateTime_ReturnsCorrectValue_OnNonNullString()
    {
        DateTime? result = JobHelper.DeserializeNullableDateTime(WellKnownTimestamp.ToString());
        Assert.Equal(WellKnownDateTime, result);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ForDeserializeCanUseCustomSerializerOptionsWithInvocationData()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);

        MethodInfo? method = typeof(BackgroundJob).GetMethod("DoWork");
        object[] args = ["123", "Test"];
        Job job = new(typeof(BackgroundJob), method, args);

        InvocationData invocationData = InvocationData.SerializeJob(job);
        Job deserializedJob = invocationData.DeserializeJob();

        Assert.Equal(typeof(BackgroundJob), deserializedJob.Type);
        Assert.Equal(method, deserializedJob.Method);
        Assert.Equal(args, deserializedJob.Args);
    }

    private interface IClass
    {
    }

    private sealed class ClassA(string propertyA) : IClass
    {
        public string PropertyA { get; } = propertyA;
    }

    private sealed class BackgroundJob
    {
        [UsedImplicitly]
        public void DoWork(string workId, string message)
        {
        }
    }
}
