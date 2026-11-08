using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class SerializationHelperFacts
    {
        private const string ClassAType = "Masterly.NightShift.Core.Tests.Common.SerializationHelperFacts+ClassA, Masterly.NightShift.Core.Tests";
        private const string CircleType = "Masterly.NightShift.Core.Tests.Common.SerializationHelperFacts+Circle, Masterly.NightShift.Core.Tests";

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

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WritesTheActualType_WhenTheDeclaredTypeIsAnInterface()
        {
            var result = SerializationHelper.Serialize(new ClassA("B"), typeof(IClass), SerializationOption.User);

            Assert.Equal($@"{{""$type"":""{ClassAType}"",""PropertyA"":""B""}}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_ReadsTheActualType_WhenTheDeclaredTypeIsAnInterface()
        {
            var result = SerializationHelper.Deserialize($@"{{""$type"":""{ClassAType}"",""PropertyA"":""B""}}", typeof(IClass), SerializationOption.User);

            var value = Assert.IsType<ClassA>(result);
            Assert.Equal("B", value.PropertyA);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeAndDeserialize_RoundTripAValue_DeclaredAsAnAbstractClass()
        {
            var json = SerializationHelper.Serialize(new Circle { Name = "wheel", Radius = 2.5 }, typeof(Shape), SerializationOption.User);
            var result = SerializationHelper.Deserialize(json, typeof(Shape), SerializationOption.User);

            Assert.StartsWith($@"{{""$type"":""{CircleType}"",", json);
            var circle = Assert.IsType<Circle>(result);
            Assert.Equal("wheel", circle.Name);
            Assert.Equal(2.5, circle.Radius);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeAndDeserialize_RoundTripAValue_DeclaredAsAnInterface_WithRecommendedSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

            var json = SerializationHelper.Serialize(new ClassA("B"), typeof(IClass), SerializationOption.User);
            var result = SerializationHelper.Deserialize(json, typeof(IClass), SerializationOption.User);

            Assert.Equal($@"{{""$type"":""{ClassAType}"",""PropertyA"":""B""}}", json);
            Assert.Equal("B", Assert.IsType<ClassA>(result).PropertyA);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeAndDeserialize_RoundTripAValue_DeclaredAsAnInterface_WithCustomUserOptions()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var json = SerializationHelper.Serialize(new ClassA("B"), typeof(IClass), SerializationOption.User);
            var result = SerializationHelper.Deserialize(json, typeof(IClass), SerializationOption.User);

            // The default encoder of these options escapes '+' in the type name
            using var document = JsonDocument.Parse(json);
            Assert.Equal(ClassAType, document.RootElement.GetProperty("$type").GetString());
            Assert.Equal("B", document.RootElement.GetProperty("propertyA").GetString());
            Assert.Equal("B", Assert.IsType<ClassA>(result).PropertyA);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_DoesNotWriteTheType_WhenTheDeclaredTypeIsConcrete_EvenForADerivedValue()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

            var result = SerializationHelper.Serialize(new DerivedOrder { Id = 1, Extra = "x" }, typeof(Order), SerializationOption.User);

            Assert.Equal(@"{""Extra"":""x"",""Id"":1}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeAndDeserialize_DoNotUseTypeNames_ForCollectionInterfaces()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

            var json = SerializationHelper.Serialize(new List<int> { 1, 2 }, typeof(IEnumerable<int>), SerializationOption.User);
            var result = SerializationHelper.Deserialize(json, typeof(IEnumerable<int>), SerializationOption.User);

            Assert.Equal("[1,2]", json);
            Assert.Equal(new[] { 1, 2 }, Assert.IsAssignableFrom<IEnumerable<int>>(result));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeAndDeserialize_UseTheSystemTextJsonDiscriminator_WhenPolymorphismIsConfigured()
        {
            var json = SerializationHelper.Serialize(new Square { Side = 2 }, typeof(IPolygon), SerializationOption.User);
            var result = SerializationHelper.Deserialize(json, typeof(IPolygon), SerializationOption.User);

            Assert.Equal(@"{""$type"":""square"",""Side"":2}", json);
            Assert.Equal(2, Assert.IsType<Square>(result).Side);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ReturnsNull_ForANullValue_DeclaredAsAnInterface()
        {
            Assert.Null(SerializationHelper.Deserialize("null", typeof(IClass), SerializationOption.User));
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ThrowsJsonException_WhenAValueDeclaredAsAnInterface_HasNoTypeName()
        {
            var exception = Assert.Throws<JsonException>(
                () => SerializationHelper.Deserialize(@"{""PropertyA"":""B""}", typeof(IClass), SerializationOption.User));

            Assert.Contains("\"$type\"", exception.Message);
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ThrowsJsonException_WhenTheTypeName_IsNotAssignableToTheDeclaredType()
        {
            var json = $@"{{""$type"":""{CircleType}"",""Radius"":1}}";

            Assert.Throws<JsonException>(() => SerializationHelper.Deserialize(json, typeof(IClass), SerializationOption.User));
        }

        [DataCompatibilityRangeFact]
        public void Deserialize_ResolvesTheTypeName_ThroughTheConfiguredTypeResolver()
        {
            try
            {
                GlobalConfiguration.Configuration.UseTypeResolver(
                    name => name == "OldCompany.ClassA, OldCompany" ? typeof(ClassA) : TypeHelper.DefaultTypeResolver(name));

                var result = SerializationHelper.Deserialize(
                    @"{""$type"":""OldCompany.ClassA, OldCompany"",""PropertyA"":""B""}", typeof(IClass), SerializationOption.User);

                Assert.Equal("B", Assert.IsType<ClassA>(result).PropertyA);
            }
            finally
            {
                GlobalConfiguration.Configuration.UseDefaultTypeResolver();
            }
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_ReadsUserDataLeniently_WhateverTheUserOptions()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var result = SerializationHelper.Deserialize<Invoice>(
                @"{""CustomerId"":""42"",""Currency"":""EUR"",""Note"":""paid"",}", SerializationOption.User);

            Assert.Equal(42, result.CustomerId);
            Assert.Equal("EUR", result.Currency);
            Assert.Equal("paid", result.Note);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WritesUserData_ExactlyAsTheUserOptionsSay()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var result = SerializationHelper.Serialize(new Invoice { CustomerId = 42, Currency = "EUR", Note = "paid" }, SerializationOption.User);

            Assert.Equal(@"{""customerId"":42,""currency"":""EUR""}", result);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_ReadsATypeWhoseNamesDifferOnlyByCase_WithCaseSensitiveUserOptions()
        {
            // Case-insensitive reading can't describe this type, so it is read with the options it was written with
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions());
            var json = SerializationHelper.Serialize(new CaseCollision { Name = "a", NAME = "b" }, SerializationOption.User);

            var first = SerializationHelper.Deserialize<CaseCollision>(json, SerializationOption.User);
            var second = SerializationHelper.Deserialize<CaseCollision>(json, SerializationOption.User);

            Assert.Equal(@"{""Name"":""a"",""NAME"":""b""}", json);
            Assert.Equal("a", first.Name);
            Assert.Equal("b", first.NAME);
            Assert.Equal("b", second.NAME);
        }

        [Fact, CleanSerializerSettings]
        public void SetUserSerializerSettings_DoesNotChangeTheGivenOptions()
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            SerializationHelper.SetUserSerializerSettings(options);
            SerializationHelper.Deserialize<Invoice>(@"{""CustomerId"":42}", SerializationOption.User);

            Assert.False(options.IsReadOnly);
            Assert.False(options.PropertyNameCaseInsensitive);
            Assert.False(options.IncludeFields);
            Assert.Empty(options.Converters);
        }

        [DataCompatibilityRangeFact]
        public void GetInternalSettings_ReturnsAModifiableCopy_OfTheInternalOptions()
        {
            var options = SerializationHelper.GetInternalSettings();

            Assert.False(options.IsReadOnly);
            Assert.Equal(JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);
            Assert.True(options.PropertyNameCaseInsensitive);
            Assert.True(options.IncludeFields);
            Assert.Equal(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals, options.NumberHandling);
            Assert.Same(JavaScriptEncoder.UnsafeRelaxedJsonEscaping, options.Encoder);
            Assert.Equal(128, options.MaxDepth);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void UserData_KeepsMembersEqualToTheirDefaultValue_WithTheDefaultOptions()
        {
            var json = SerializationHelper.Serialize(new ReportRequest { Days = 30, Limit = 0 }, SerializationOption.User);
            var result = SerializationHelper.Deserialize<ReportRequest>(json, SerializationOption.User);

            Assert.Equal(@"{""Days"":30,""Limit"":0}", json);
            Assert.Equal(30, result.Days);
            Assert.Equal(0, result.Limit);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void UserData_KeepsMembersEqualToTheirDefaultValue_WithTheRecommendedOptions()
        {
            SerializationHelper.SetRecommendedSerializerSettings(null);

            var json = SerializationHelper.Serialize(new ReportRequest { Days = 30, Limit = 0 }, SerializationOption.User);
            var result = SerializationHelper.Deserialize<ReportRequest>(json, SerializationOption.User);

            Assert.Equal(@"{""Days"":30,""Limit"":0}", json);
            Assert.Equal(30, result.Days);
            Assert.Equal(0, result.Limit);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void InternalData_KeepsMembersOfOtherTypes_EqualToTheirDefaultValue()
        {
            var json = SerializationHelper.Serialize(new ReportRequest { Days = 30, Limit = 0 });

            Assert.Equal(@"{""Days"":30,""Limit"":0}", json);
        }

        [Theory, CleanSerializerSettings]
        [InlineData(false)]
        [InlineData(true)]
        public void UserData_GetsTheDefaultValue_OfMembersMissingFromTheJson(bool recommended)
        {
            // What the previous serializer stored with DefaultValueHandling.IgnoreAndPopulate
            if (recommended) SerializationHelper.SetRecommendedSerializerSettings(null);

            var result = SerializationHelper.Deserialize<ReportRequest>("{}", SerializationOption.User);

            Assert.Equal(30, result.Days);
            Assert.Equal(0, result.Limit);
        }

        [Fact]
        public void InternalData_GetsTheDefaultValue_OfMembersMissingFromTheJson()
        {
            var result = SerializationHelper.Deserialize<ReportRequest>(@"{""Days"":7}");

            Assert.Equal(7, result.Days);
            Assert.Equal(0, result.Limit);
        }

        [Fact, CleanSerializerSettings]
        public void UserData_KeepsConstructorParameters_WhenMembersHaveADefaultValue()
        {
            var result = SerializationHelper.Deserialize<BoundReport>(@"{""Days"":5}", SerializationOption.User);

            Assert.Equal(5, result.Days);
            Assert.Equal(7, result.Window);
        }

        [Fact, CleanSerializerSettings]
        public void UserData_StillCallsTheTypesOwnOnDeserializing_AfterSettingTheDefaultValues()
        {
            var result = SerializationHelper.Deserialize<CallbackReport>(@"{""Days"":3}", SerializationOption.User);

            Assert.Equal(30, result.DaysWhenDeserializing);
            Assert.Equal(3, result.Days);
        }

        [Fact, CleanSerializerSettings]
        public void UserData_WithItsOwnOptions_KeepsMembersEqualToTheirDefaultValue()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions());

            var json = SerializationHelper.Serialize(new ReportRequest { Days = 30, Limit = 0 }, SerializationOption.User);

            Assert.Equal(@"{""Days"":30,""Limit"":0}", json);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WritesNaNAndInfinities_AsText_AndReadsThemBack()
        {
            Assert.Equal(@"""NaN""", SerializationHelper.Serialize(double.NaN, SerializationOption.User));
            Assert.Equal(@"""Infinity""", SerializationHelper.Serialize(float.PositiveInfinity, SerializationOption.User));
            Assert.Equal(@"""-Infinity""", SerializationHelper.Serialize<object>(double.NegativeInfinity, SerializationOption.User));
            Assert.Equal(@"""NaN""", SerializationHelper.Serialize(double.NaN));

            Assert.True(double.IsNaN(SerializationHelper.Deserialize<double>(@"""NaN""", SerializationOption.User)));
            Assert.True(float.IsPositiveInfinity(SerializationHelper.Deserialize<float>(@"""Infinity""")));
        }

        [Fact, CleanSerializerSettings]
        public void Serialize_WritesNaN_WithTheRecommendedOptions()
        {
            SerializationHelper.SetRecommendedSerializerSettings(null);

            Assert.Equal(@"""NaN""", SerializationHelper.Serialize(double.NaN, SerializationOption.User));
        }

        [Fact, CleanSerializerSettings]
        public void Deserialize_ReadsNaN_EvenWhenTheUserOptionsDoNotWriteIt()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions());

            Assert.True(double.IsNaN(SerializationHelper.Deserialize<double>(@"""NaN""", SerializationOption.User)));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeInternal_IgnoresTheUserOptions_AtEveryLevel()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = new KnownTypesResolver(typeof(int))
            });

            Assert.Equal(@"{""PropertyA"":""A""}", SerializationHelper.SerializeInternal(new ClassA("A")));
            Assert.Equal(@"[""a"",""b""]", SerializationHelper.SerializeInternal(new[] { "a", "b" }));
            Assert.Equal(@"""b1""", SerializationHelper.SerializeInternal((object)"b1"));
            Assert.Null(SerializationHelper.SerializeInternal(null));
        }

        private sealed class ReportRequest
        {
            [System.ComponentModel.DefaultValue(30)]
            public int Days { get; set; }

            [System.ComponentModel.DefaultValue(0)]
            public int Limit { get; set; } = 100;
        }

        private sealed class BoundReport
        {
            public BoundReport(int days)
            {
                Days = days;
            }

            [System.ComponentModel.DefaultValue(30)]
            public int Days { get; set; }

            [System.ComponentModel.DefaultValue(7)]
            public int Window { get; set; }
        }

        private sealed class CallbackReport : IJsonOnDeserializing
        {
            [System.ComponentModel.DefaultValue(30)]
            public int Days { get; set; }

            [JsonIgnore]
            public int DaysWhenDeserializing { get; private set; }

            void IJsonOnDeserializing.OnDeserializing()
            {
                DaysWhenDeserializing = Days;
            }
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void SerializeJob_AndDeserializeJob_Work_WhenTheApplicationResolverOnlyKnowsTheArgumentTypes()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int), typeof(string)) });
            var job = Job.FromExpression(() => JobService.Run(5, "a"));

            var data = InvocationData.SerializeJob(job);
            var back = InvocationData.DeserializePayload(data.SerializePayload()).DeserializeJob();

            Assert.Equal(5, back.Args[0]);
            Assert.Equal("a", back.Args[1]);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_WritesLibraryData_WithTheInternalOptions_WhenTheApplicationResolverDoesNotKnowIt()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int)) });

            var stateData = SerializationHelper.Serialize(new Dictionary<string, string> { ["Queue"] = "default" });
            var list = SerializationHelper.Serialize(new List<string> { "a" });
            var exception = SerializationHelper.Serialize(new ExceptionInfo(new InvalidOperationException("boom")), SerializationOption.User);

            Assert.Equal("{\"Queue\":\"default\"}", stateData);
            Assert.Equal("[\"a\"]", list);
            Assert.Equal("boom", SerializationHelper.Deserialize<ExceptionInfo>(exception).Message);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Serialize_StillThrows_ForUserData_TheApplicationResolverDoesNotKnow()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int)) });

            Assert.Throws<NotSupportedException>(() => SerializationHelper.Serialize(new ClassA("A"), SerializationOption.User));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void DeserializePayload_ReadsAPayloadInTheVersion170Format_WhenTheApplicationResolverDoesNotKnowStringArrays()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int), typeof(string)) });
            var payload = SerializationHelper.SerializeInternal(new Dictionary<string, object>
            {
                ["t"] = typeof(JobService).AssemblyQualifiedName,
                ["m"] = nameof(JobService.Run),
                ["p"] = new[] { typeof(int).AssemblyQualifiedName, typeof(string).AssemblyQualifiedName },
                ["a"] = new[] { "5", "\"a\"" },
            });

            var job = InvocationData.DeserializePayload(payload).DeserializeJob();

            Assert.Equal(5, job.Args[0]);
            Assert.Equal("a", job.Args[1]);
        }

        public static class JobService
        {
            // ReSharper disable once UnusedParameter.Global
            public static void Run(int number, string text)
            {
            }
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

        private abstract class Shape
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public string Name { get; set; }
        }

        private sealed class Circle : Shape
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public double Radius { get; set; }
        }

        private class Order
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public int Id { get; set; }
        }

        private sealed class DerivedOrder : Order
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public string Extra { get; set; }
        }

        [JsonDerivedType(typeof(Square), "square")]
        private interface IPolygon
        {
        }

        private sealed class Square : IPolygon
        {
            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public int Side { get; set; }
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        private sealed class CaseCollision
        {
            public string Name { get; set; }
            public string NAME { get; set; }
        }

        private sealed class Invoice
        {
            // ReSharper disable UnusedAutoPropertyAccessor.Local
            public int CustomerId { get; set; }
            public string Currency { get; set; }
            // ReSharper restore UnusedAutoPropertyAccessor.Local

            // Public fields were written by the previous serializer
            public string Note;
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
