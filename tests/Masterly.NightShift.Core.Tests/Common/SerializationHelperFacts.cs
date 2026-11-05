using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class SerializationHelperFacts
    {
        private const string ClassAType = "Masterly.NightShift.Core.Tests.Common.SerializationHelperFacts+ClassA, Masterly.NightShift.Core.Tests";

        [DataCompatibilityRangeFact]
        public void Serialize_ReturnsNull_WhenValueIsNull()
        {
            Assert.Null(SerializationHelper.Serialize((string)null));
        }

        [DataCompatibilityRangeFact]
        public void Serialize_ReturnsCorrectResult_WhenValueIsString()
        {
            var result = SerializationHelper.Serialize("Simple string");
            Assert.Equal("\"Simple string\"", result);
        }

        [DataCompatibilityRangeFact]
        public void Serialize_ReturnsCorrectValue_WhenValueIsCustomObject()
        {
            var result = SerializationHelper.Serialize(new ClassA("B"));
            Assert.Equal(@"{""PropertyA"":""B""}", result);
        }

        [DataCompatibilityRangeFact]
        public void Serialize_KeepsNonAsciiText_Readable()
        {
            var result = SerializationHelper.Serialize("مرحبا ü");
            Assert.Equal("\"مرحبا ü\"", result);
        }

        [DataCompatibilityRangeFact]
        public void Serialize_IncludesTheTypeName_WhenOptionIsTypedInternal_AndTheValueIsDeclaredAsObject()
        {
            var result = SerializationHelper.Serialize<object>(new ClassA("B"), SerializationOption.TypedInternal);
            Assert.Equal($@"{{""$type"":""{ClassAType}"",""PropertyA"":""B""}}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_SerializesWithUserSettings_WhenOptionsIsUser()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var result = SerializationHelper.Serialize(new ClassA("A"), SerializationOption.User);
            Assert.Equal(@"{""propertyA"":""A""}", result);
        }

        [DataCompatibilityRangeFact(MaxExcludingLevel = CompatibilityLevel.Version_170), CleanSerializerSettings]
        public void Serialize_SerializesWithUserSettings_WhenOptionsIsInternal_BeforeVersion170()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var result = SerializationHelper.Serialize(new ClassA("A"));
            Assert.Equal(@"{""propertyA"":""A""}", result);
        }

        [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170), CleanSerializerSettings]
        public void Serialize_DoesNotSerializeWithUserSettings_WhenOptionsIsInternal_AfterVersion170()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var result = SerializationHelper.Serialize(new ClassA("A"));
            Assert.Equal(@"{""PropertyA"":""A""}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_DoesNotSerializeWithUserSettings_WhenOptionsIsTypedInternal()
        {
            SerializationHelper.SetUserSerializerSettings(SerializerSettingsHelper.DangerousSettings);

            var result = SerializationHelper.Serialize(new ClassB { StringValue = "hi" }, SerializationOption.TypedInternal);
            Assert.Equal(@"{""StringValue"":""hi"",""DefaultValue"":0}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_ProducesJson_ThatPlainSystemTextJsonCanRead()
        {
            SerializationHelper.SetUserSerializerSettings(SerializerSettingsHelper.DangerousSettings);
            var initialObject = new ClassA("A");

            var result = SerializationHelper.Serialize(initialObject);
            var resultingObject = JsonSerializer.Deserialize<ClassA>(result, new JsonSerializerOptions(SerializerSettingsHelper.DangerousSettings) { PropertyNameCaseInsensitive = true });

            Assert.Equal(initialObject.PropertyA, resultingObject.PropertyA);
        }

        [DataCompatibilityRangeFact(MinLevel = CompatibilityLevel.Version_170), CleanSerializerSettings]
        public void Serialize_WritesDatesInIsoFormat_WhatEverTheUserSettings()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new DayMonthYearConverter() } });

            var result = SerializationHelper.Serialize(new ClassB { StringValue = "B", DateTimeValue = new DateTime(1961, 4, 12) });
            Assert.Equal(@"{""StringValue"":""B"",""DefaultValue"":0,""DateTimeValue"":""1961-04-12T00:00:00""}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WithRecommendedSettings_DoesNotIncludeTypeProperty_WhenTheTypeIsDeclared()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

            var result = SerializationHelper.Serialize(new ClassA("hello"), typeof(ClassA), SerializationOption.User);

            Assert.Equal("{\"PropertyA\":\"hello\"}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WithRecommendedSettings_IncludesTypeProperty_WhenTheValueIsDeclaredAsObject()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

            var result = SerializationHelper.Serialize(new ClassA("hello"), typeof(object), SerializationOption.User);

            Assert.Equal($"{{\"$type\":\"{ClassAType}\",\"PropertyA\":\"hello\"}}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WithDefaultUserSettings_DoesNotIncludeTypeProperty_EvenWhenTheValueIsDeclaredAsObject()
        {
            var result = SerializationHelper.Serialize(new ClassA("hello"), typeof(object), SerializationOption.User);

            Assert.Equal("{\"PropertyA\":\"hello\"}", result);
        }

        [DataCompatibilityRangeFact]
        public void Serialize_WritesAState_WithItsTypeName_AndDeserializeReadsItBack()
        {
            IState state = new EnqueuedState("critical") { Reason = "test" };

            var json = SerializationHelper.Serialize(state, SerializationOption.TypedInternal);
            var result = SerializationHelper.Deserialize<IState>(json, SerializationOption.TypedInternal);

            Assert.StartsWith("{\"$type\":\"Masterly.NightShift.States.EnqueuedState, Masterly.NightShift.Core", json);
            var enqueued = Assert.IsType<EnqueuedState>(result);
            Assert.Equal("critical", enqueued.Queue);
            Assert.Equal("test", enqueued.Reason);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReadsAState_StoredInThePreviousFormat()
        {
            var json = "{\"$type\":\"Masterly.NightShift.States.ScheduledState, Masterly.NightShift.Core\",\"EnqueueAt\":\"2026-11-05T10:00:00Z\",\"Reason\":\"later\"}";

            var result = SerializationHelper.Deserialize<IState>(json, SerializationOption.TypedInternal);

            var scheduled = Assert.IsType<ScheduledState>(result);
            Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), scheduled.EnqueueAt);
            Assert.Equal("later", scheduled.Reason);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ThrowsJsonException_WhenAStateHasNoTypeName()
        {
            Assert.ThrowsAny<JsonException>(() => SerializationHelper.Deserialize<IState>("{\"Queue\":\"default\"}", SerializationOption.TypedInternal));
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReadsEnums_AsNumbersOrNames()
        {
            Assert.Equal(DayOfWeek.Tuesday, SerializationHelper.Deserialize<DayOfWeek>("2"));
            Assert.Equal(DayOfWeek.Tuesday, SerializationHelper.Deserialize<DayOfWeek>("\"tuesday\""));
            Assert.Equal("2", SerializationHelper.Serialize(DayOfWeek.Tuesday));
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReadsQuotedNumbers_AndMatchesPropertyNamesIgnoringCase()
        {
            var result = SerializationHelper.Deserialize<ClassB>("{\"stringvalue\":\"x\",\"defaultValue\":\"42\"}");

            Assert.Equal("x", result.StringValue);
            Assert.Equal(42, result.DefaultValue);
        }

        [DataCompatibilityRangeTheory]
        [InlineData("true", typeof(bool))]
        [InlineData("42", typeof(long))]
        [InlineData("4.5", typeof(double))]
        [InlineData("\"text\"", typeof(string))]
        [InlineData("\"2026-11-05T10:00:00Z\"", typeof(DateTime))]
        [InlineData("{\"a\":1}", typeof(JsonElement))]
        public void Deserialize_ReadsValuesDeclaredAsObject_AsPrimitivesWherePossible(string json, Type expected)
        {
            var result = SerializationHelper.Deserialize<object>(json);

            Assert.IsType(expected, result);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReadsAnArray_WrappedWithTypeAndValues()
        {
            var result = SerializationHelper.Deserialize<string[]>("{\"$type\":\"System.String[], mscorlib\",\"$values\":[\"a\",\"b\"]}");

            Assert.Equal(new[] { "a", "b" }, result);
        }

        [DataCompatibilityRangeFact]
        public void Serialize_WritesTypes_AsTheirNames_AndDeserializeReadsThemBack()
        {
            var json = SerializationHelper.Serialize(new[] { typeof(string) });
            var result = SerializationHelper.Deserialize<Type[]>(json);

            Assert.Equal(new[] { typeof(string) }, result);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReturnsNull_WhenValueIsNull()
        {
            var result = SerializationHelper.Deserialize(null, typeof(string));
            Assert.Null(result);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ThrowsException_WhenTypeIsNull()
        {
            // ReSharper disable once AssignNullToNotNullAttribute
            var exception = Assert.Throws<ArgumentNullException>(() => SerializationHelper.Deserialize("someString", null));
            Assert.Equal("type", exception.ParamName);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReturnsCorrectValue_WhenValueIsString()
        {
            var result = SerializationHelper.Deserialize("\"hello\"", typeof(string));
            Assert.Equal("hello", result);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReturnsCorrectObject_WhenTypeIsCustomClass()
        {
            var value = SerializationHelper.Deserialize(@"{""PropertyA"":""A""}", typeof(ClassA)) as ClassA;

            Assert.NotNull(value);
            Assert.Equal("A", value.PropertyA);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_IgnoresTheTypeName_WhenTheTypeIsKnown()
        {
            var valueJson = $@"{{""$type"":""{ClassAType}"",""PropertyA"":""A""}}";

            var value = SerializationHelper.Deserialize(valueJson, typeof(ClassA), SerializationOption.TypedInternal) as ClassA;

            Assert.NotNull(value);
            Assert.Equal("A", value.PropertyA);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        // Older internal data was written with the user settings, so they are the fallback
        public void Deserialize_HandlesDeserializationUsingUserOption_WhenUsingInternalOptionThrewException()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new DayMonthYearConverter() } });

            var value = SerializationHelper.Deserialize("{\"StringValue\":\"B\",\"DateTimeValue\":\"12041961\"}", typeof(ClassB)) as ClassB;

            Assert.NotNull(value);
            Assert.Equal("B", value.StringValue);
            Assert.Equal(new DateTime(1961, 4, 12), value.DateTimeValue);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_RethrowsTheOriginalException_WhenTheUserSettingsFailToo()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new DayMonthYearConverter() } });

            Assert.ThrowsAny<JsonException>(() => SerializationHelper.Deserialize("{\"DateTimeValue\":\"not a date\"}", typeof(ClassB)));
        }

        [DataCompatibilityRangeFact]
        public void DeserializeGeneric_ReturnsNull_WhenValueIsNull()
        {
            var result = SerializationHelper.Deserialize<object>(null);
            Assert.Null(result);
        }

        [DataCompatibilityRangeFact]
        public void DeserializeGeneric_ReturnsDefaultValue_WhenGenericArgumentIsValueType()
        {
            var result = SerializationHelper.Deserialize<int>(null);
            Assert.Equal(0, result);
        }

        [DataCompatibilityRangeFact]
        public void DeserializeGeneric_ReturnsCorrectValue_WhenValueIsString()
        {
            var result = SerializationHelper.Deserialize<string>("\"hello\"");
            Assert.Equal("hello", result);
        }

        [DataCompatibilityRangeFact]
        public void DeserializeGeneric_ReturnsCorrectObject_WhenTypeIsCustomClass()
        {
            var value = SerializationHelper.Deserialize<ClassA>(@"{""PropertyA"":""A""}");

            Assert.NotNull(value);
            Assert.Equal("A", value.PropertyA);
        }

        [DataCompatibilityRangeFact]
        public void DeserializeGeneric_ReturnsCorrectObject_WhenOptionsIsTypedInternal()
        {
            var value = SerializationHelper.Deserialize<ClassA>(@"{""PropertyA"":""A""}", SerializationOption.TypedInternal);

            Assert.NotNull(value);
            Assert.Equal("A", value.PropertyA);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void DeserializeGeneric_HandlesUsingUserOption_WhenUsingTypedInternalOptionThrewException()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new DayMonthYearConverter() } });

            var value = SerializationHelper.Deserialize<ClassB>("{\"DateTimeValue\":\"01022003\"}", SerializationOption.TypedInternal);

            Assert.Equal(new DateTime(2003, 2, 1), value.DateTimeValue);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void DeserializeGeneric_RethrowsJsonException_WhenValueHasIncorrectFormat()
        {
            Assert.ThrowsAny<JsonException>(() => SerializationHelper.Deserialize<ClassA>("asdfaljsadkfh"));
        }

        [DataCompatibilityRangeFact]
        public void GetInternalSettings_ReturnsAModifiableCopy_OfTheInternalOptions()
        {
            var options = SerializationHelper.GetInternalSettings();

            Assert.False(options.IsReadOnly);
            Assert.Equal(JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);
            Assert.True(options.PropertyNameCaseInsensitive);
            Assert.True(options.IncludeFields);
            Assert.Equal(JsonNumberHandling.AllowReadingFromString, options.NumberHandling);
            Assert.Same(JavaScriptEncoder.UnsafeRelaxedJsonEscaping, options.Encoder);
            Assert.Equal(128, options.MaxDepth);
        }

        private interface IClass
        {
        }

        private class ClassA : IClass
        {
            public ClassA(string propertyA)
            {
                PropertyA = propertyA;
            }

            public string PropertyA { get; }
        }

        private class ClassB
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public string StringValue { get; set; }

            // ReSharper disable once UnusedMember.Local
            public object NullValue { get; set; }

            // ReSharper disable once UnusedMember.Local
            public int DefaultValue { get; set; }

            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public DateTime? DateTimeValue { get; set; }
        }

        /// <summary>Reads and writes dates as ddMMyyyy, a format the internal options can't read.</summary>
        private sealed class DayMonthYearConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return DateTime.ParseExact(reader.GetString()!, "ddMMyyyy", CultureInfo.InvariantCulture);
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString("ddMMyyyy", CultureInfo.InvariantCulture));
            }
        }
    }
}
