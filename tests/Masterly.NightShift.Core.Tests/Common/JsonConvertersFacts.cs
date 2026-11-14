using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Range = System.Range;

namespace Masterly.NightShift.Core.Tests.Common;

public class JsonConvertersFacts
{
    private static readonly string GadgetType = typeof(Gadget).AssemblyQualifiedName!;

    // S1: a "$type" in user data never selects the type it is read back as

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void DictionaryWithATypeKey_PassedAsObject_IsReadBackAsTheDictionary()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Gadget.Constructed = 0;
        Dictionary<string, string> form = new() { ["$type"] = GadgetType, ["Command"] = "calc.exe" };
        Job job = Job.FromExpression(() => FormRelay.Handle(form));

        object back = InvocationData.SerializeJob(job).DeserializeJob().Args[0];

        Dictionary<string, string> dictionary = Assert.IsType<Dictionary<string, string>>(back);
        Assert.Equal(form, dictionary);
        Assert.Equal(0, Gadget.Constructed);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void DictionaryWithAValuesKey_PassedAsObject_IsReadBackAsTheDictionary()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Dictionary<string, string> form = new() { ["$values"] = "x" };
        Dictionary<string, object> nested = new() { ["$type"] = GadgetType, ["$values"] = new List<int> { 1 }, ["n"] = 2L };

        object formBack = RoundTripAsObject(form);
        object nestedBack = RoundTripAsObject(nested);

        Assert.Equal(form, Assert.IsType<Dictionary<string, string>>(formBack));
        Dictionary<string, object> nestedDictionary = Assert.IsType<Dictionary<string, object>>(nestedBack);
        Assert.Equal(GadgetType, nestedDictionary["$type"]);
        Assert.Equal("[1]", Assert.IsType<JsonElement>(nestedDictionary["$values"]).GetRawText());
        Assert.Equal(2L, nestedDictionary["n"]);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void JsonElementWithATypeProperty_PassedAsObject_IsReadBackAsTheJsonElement()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Gadget.Constructed = 0;
        using JsonDocument document = JsonDocument.Parse($"{{\"$type\":\"{GadgetType}\",\"Command\":\"calc.exe\"}}");

        object back = RoundTripAsObject(document.RootElement);

        JsonElement element = Assert.IsType<JsonElement>(back);
        Assert.Equal(GadgetType, element.GetProperty("$type").GetString());
        Assert.Equal("calc.exe", element.GetProperty("Command").GetString());
        Assert.Equal(0, Gadget.Constructed);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ObjectValue_WithATypePropertyThatIsNotFirst_IsNotReadAsThatType()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Gadget.Constructed = 0;

        object? back = SerializationHelper.Deserialize(
            $"{{\"Command\":\"calc.exe\",\"$type\":\"{GadgetType}\"}}", typeof(object), SerializationOption.User);

        Assert.IsType<JsonElement>(back);
        Assert.Equal(0, Gadget.Constructed);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ObjectValue_WithALeadingTypeProperty_IsStillReadAsThatType()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

        object? gadget = SerializationHelper.Deserialize(
            $"{{\"$type\":\"{GadgetType}\",\"Command\":\"ping\"}}", typeof(object), SerializationOption.User);
        object? list = SerializationHelper.Deserialize(
            "{\"$type\":\"System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib\",\"$values\":[1,2]}",
            typeof(object),
            SerializationOption.User);

        Assert.Equal("ping", Assert.IsType<Gadget>(gadget).Command);
        Assert.Equal([1, 2], Assert.IsType<List<int>>(list));
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ValueDeclaredAsAnInterface_WithATypeMember_RoundTrips()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Labelled value = new() { TypeName = "invoice", Values = "a,b" };

        string json = SerializationHelper.Serialize(value, typeof(ILabelled), SerializationOption.User);
        object? back = SerializationHelper.Deserialize(json, typeof(ILabelled), SerializationOption.User);

        Labelled labelled = Assert.IsType<Labelled>(back);
        Assert.Equal("invoice", labelled.TypeName);
        Assert.Equal("a,b", labelled.Values);
    }

    // S3: types written as their read-only members

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void BigInteger_RoundTrips(bool recommended)
    {
        Configure(recommended);
        BigInteger amount = BigInteger.Parse("-123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture);

        Job job = Job.FromExpression(() => Sink.Pay(amount));
        InvocationData data = InvocationData.SerializeJob(job);

        Assert.Equal(amount, data.DeserializeJob().Args[0]);
        Assert.Equal(amount, SerializationHelper.Deserialize<BigInteger>(SerializationHelper.Serialize(amount, SerializationOption.User), SerializationOption.User));
        Assert.Equal(amount, SerializationHelper.Deserialize<BigInteger>(SerializationHelper.Serialize(amount)));
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void BigInteger_WrittenByHangfire_AsANumberOrAString_IsRead(bool recommended)
    {
        Configure(recommended);
        string parameterTypes = SerializationHelper.Serialize(new[] { typeof(BigInteger).AssemblyQualifiedName });

        InvocationData number = new(typeof(Sink).AssemblyQualifiedName!, nameof(Sink.Pay), parameterTypes, "[\"123456789012345678901234567890\"]");
        InvocationData text = new(typeof(Sink).AssemblyQualifiedName!, nameof(Sink.Pay), parameterTypes, "[\"\\\"42\\\"\"]");

        Assert.Equal(BigInteger.Parse("123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture), number.DeserializeJob().Args[0]);
        Assert.Equal(new BigInteger(42), text.DeserializeJob().Args[0]);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void Color_RoundTrips(bool recommended)
    {
        Configure(recommended);
        Color named = Color.Red;
        Color custom = Color.FromArgb(128, 10, 20, 30);

        Job job = Job.FromExpression(() => Sink.Paint(named, custom));
        Job back = InvocationData.SerializeJob(job).DeserializeJob();

        Assert.Equal(named, back.Args[0]);
        Assert.Equal(custom.ToArgb(), ((Color)back.Args[1]).ToArgb());
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexRangeAndRune_RoundTrip(bool recommended)
    {
        Configure(recommended);
        Index index = ^1;
        Range range = 1..^2;
        Rune rune = new(0x1F600);

        Job job = Job.FromExpression(() => Sink.Slice(index, range, rune));
        Job back = InvocationData.SerializeJob(job).DeserializeJob();

        Assert.Equal(index, back.Args[0]);
        Assert.Equal(range, back.Args[1]);
        Assert.Equal(rune, back.Args[2]);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexAndRange_WrittenAsTheirMembers_AreRead(bool recommended)
    {
        Configure(recommended);

        Index index = SerializationHelper.Deserialize<Index>("{\"Value\":1,\"IsFromEnd\":true}", SerializationOption.User);
        Range range = SerializationHelper.Deserialize<Range>(
            "{\"Start\":{\"Value\":1,\"IsFromEnd\":false},\"End\":{\"Value\":2,\"IsFromEnd\":true}}", SerializationOption.User);

        Assert.Equal(^1, index);
        Assert.Equal(1..^2, range);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeZoneInfo_RoundTrips(bool recommended)
    {
        Configure(recommended);
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        TimeZoneInfo custom = TimeZoneInfo.CreateCustomTimeZone("NightShift Test Zone", TimeSpan.FromHours(5.5), "Test Zone", "Test Standard");

        Job job = Job.FromExpression(() => Sink.At(utc, custom));
        Job back = InvocationData.SerializeJob(job).DeserializeJob();

        Assert.Equal(utc.Id, ((TimeZoneInfo)back.Args[0]).Id);
        TimeZoneInfo customBack = (TimeZoneInfo)back.Args[1];
        Assert.Equal(custom.Id, customBack.Id);
        Assert.Equal(custom.BaseUtcOffset, customBack.BaseUtcOffset);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeZoneInfo_WrittenAsItsMembers_IsRead(bool recommended)
    {
        Configure(recommended);

        TimeZoneInfo? zone = SerializationHelper.Deserialize<TimeZoneInfo>(
            "{\"Id\":\"UTC\",\"DisplayName\":\"(UTC) Coordinated Universal Time\",\"BaseUtcOffset\":\"00:00:00\"}", SerializationOption.User);

        Assert.Equal(TimeSpan.Zero, zone!.BaseUtcOffset);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeJob_RefusesAValueType_ThatWouldNotReadBack(bool recommended)
    {
        Configure(recommended);
        Temperature temperature = new(21.5);
        ConsoleKeyInfo key = new('a', ConsoleKey.A, false, false, false);

        NotSupportedException lossyStruct = Assert.Throws<NotSupportedException>(
            () => InvocationData.SerializeJob(Job.FromExpression(() => Sink.Measure(temperature))));
        Assert.Throws<NotSupportedException>(
            () => InvocationData.SerializeJob(Job.FromExpression(() => Sink.Press(key))));

        Assert.Contains(nameof(Temperature), lossyStruct.Message, StringComparison.Ordinal);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeJob_RefusesAValueType_ThatWouldNotReadBack_InAJobCreatedWithItsConstructor(bool recommended)
    {
        Configure(recommended);
        Job job = new(typeof(Sink), typeof(Sink).GetMethod(nameof(Sink.Measure))!, [new Temperature(21.5)]);

        Assert.Throws<NotSupportedException>(() => InvocationData.SerializeJob(job));
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeJob_WritesAJobReadFromStorage_WhoseValueTypeIsReadAsItsDefault(bool recommended)
    {
        Configure(recommended);
        string parameterTypes = SerializationHelper.Serialize(new[] { typeof(Temperature).AssemblyQualifiedName });
        InvocationData stored = new(typeof(Sink).AssemblyQualifiedName!, nameof(Sink.Measure), parameterTypes, "[\"{\\\"Celsius\\\":21.5}\"]");
        Job job = stored.DeserializeJob();

        InvocationData again = InvocationData.SerializeJob(job);

        Assert.Equal(default(Temperature), again.DeserializeJob().Args[0]);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeJob_KeepsAValueType_WhoseValueIsWrittenAsItsDefault(bool recommended)
    {
        Configure(recommended);
        ComputedOnly marker = default;
        Temperature zero = default;

        Job back = InvocationData.SerializeJob(Job.FromExpression(() => Sink.Mark(marker, zero))).DeserializeJob();

        Assert.Equal<object>([marker, zero], back.Args);
    }

    // S1 with Newtonsoft.Json's reference ids: "$id" before "$type"

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ObjectValue_WithAReferenceIdBeforeItsType_IsReadAsThatType()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

        object? list = SerializationHelper.Deserialize(
            "{\"$id\":\"1\",\"$type\":\"System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib\",\"$values\":[1,2]}",
            typeof(object),
            SerializationOption.User);
        object? gadget = SerializationHelper.Deserialize(
            $"{{\"$id\":\"1\",\"$type\":\"{GadgetType}\",\"Command\":\"ping\"}}", typeof(object), SerializationOption.User);
        object? dictionary = SerializationHelper.Deserialize(
            "{\"$id\":\"1\",\"$type\":\"System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Int32, mscorlib]], mscorlib\",\"a\":1}",
            typeof(Dictionary<string, int>),
            SerializationOption.User);

        Assert.Equal([1, 2], Assert.IsType<List<int>>(list));
        Assert.Equal("ping", Assert.IsType<Gadget>(gadget).Command);
        Assert.Equal(new Dictionary<string, int> { ["a"] = 1 }, dictionary);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void ValueWithAReferenceIdKeyBeforeATypeKey_PassedAsObject_IsReadBackAsItself()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        Gadget.Constructed = 0;
        Dictionary<string, string> form = new() { ["$id"] = "1", ["$type"] = GadgetType, ["Command"] = "calc.exe" };
        Dictionary<string, object> referenceOnly = new() { ["$id"] = "1", ["n"] = 2L };
        using JsonDocument document = JsonDocument.Parse($"{{\"$id\":\"1\",\"$type\":\"{GadgetType}\",\"Command\":\"calc.exe\"}}");

        object formBack = RoundTripAsObject(form);
        object referenceOnlyBack = RoundTripAsObject(referenceOnly);
        object elementBack = RoundTripAsObject(document.RootElement);

        Assert.Equal(form, Assert.IsType<Dictionary<string, string>>(formBack));
        Assert.Equal(referenceOnly, Assert.IsType<Dictionary<string, object>>(referenceOnlyBack));
        Assert.Equal(document.RootElement.GetRawText(), Assert.IsType<JsonElement>(elementBack).GetRawText());
        Assert.Equal(0, Gadget.Constructed);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeJob_KeepsValueTypes_ThatReadBack(bool recommended)
    {
        Configure(recommended);
        Money money = new(1.5m, "EUR");
        Complex complex = new(1, 2);
        KeyValuePair<string, int> pair = new("a", 1);
        (int, string) tuple = (1, "a");
        Point point = new(3, 4);
        DateOnly date = new(2026, 11, 14);
        Bound bound = new(7);
        Mutable mutable = new() { Value = 9 };
        int? nullable = 5;

        Job job = Job.FromExpression(() => Sink.Keep(money, complex, pair, tuple, point, date, bound, mutable, nullable));
        Job back = InvocationData.SerializeJob(job).DeserializeJob();

        Assert.Equal<object>([money, complex, pair, tuple, point, date, bound, mutable, nullable], back.Args);
    }

    [DataCompatibilityRangeTheory, CleanSerializerSettings]
    [InlineData(false)]
    [InlineData(true)]
    public void TypeConverterFactory_LeavesTypesThatRoundTrip_AsObjects(bool recommended)
    {
        Configure(recommended);

        string point = SerializationHelper.Serialize(new Point(3, 4), SerializationOption.User);
        string withConverter = SerializationHelper.Serialize(new Converted { Name = "n" }, SerializationOption.User);

        Assert.Equal("{\"IsEmpty\":false,\"X\":3,\"Y\":4}", point);
        Assert.Equal("{\"Name\":\"n\"}", withConverter);
    }

    // S4: text typed in the dashboard never goes through the type resolver unchecked

    [Fact, CleanSerializerSettings, GlobalLock(Reason = "Changes the type resolver")]
    public void DeserializeInput_DoesNotResolve_TheLeadingTypeOfADictionary()
    {
        List<string> resolved = [];
        string name = typeof(Dictionary<string, string>).AssemblyQualifiedName!;
        try
        {
            GlobalConfiguration.Configuration.UseTypeResolver(x => { resolved.Add(x); return TypeHelper.DefaultTypeResolver(x); });

            object? result = SerializationHelper.DeserializeInput(
                $"{{\"$type\":{JsonSerializer.Serialize(name)},\"k\":\"v\"}}", typeof(Dictionary<string, string>));

            Dictionary<string, string> dictionary = Assert.IsType<Dictionary<string, string>>(result);
            Assert.Equal(name, dictionary["$type"]);
            Assert.Equal("v", dictionary["k"]);
            Assert.Empty(resolved);
        }
        finally
        {
            GlobalConfiguration.Configuration.UseDefaultTypeResolver();
        }
    }

    [Fact, CleanSerializerSettings, GlobalLock(Reason = "Changes the type resolver")]
    public void DeserializeInput_ChecksTheTypeOfAnInterfaceValue_BeforeResolvingIt()
    {
        List<string> resolved = [];
        try
        {
            GlobalConfiguration.Configuration.UseTypeResolver(x => { resolved.Add(x); return TypeHelper.DefaultTypeResolver(x); });

            Assert.Throws<JsonException>(() => SerializationHelper.DeserializeInput(
                "{\"$type\":\"Not.Loaded.Type, Not.Loaded.Assembly\",\"Name\":\"n\"}", typeof(ILabelled)));
            Assert.Throws<JsonException>(() => SerializationHelper.DeserializeInput(
                $"{{\"$type\":\"{GadgetType}\",\"Command\":\"calc.exe\"}}", typeof(ILabelled)));
            Assert.Empty(resolved);

            object? named = SerializationHelper.DeserializeInput(
                $"{{\"$type\":\"{typeof(Named).AssemblyQualifiedName}\",\"Name\":\"n\"}}", typeof(ILabelled));
            Assert.Equal("n", Assert.IsType<Named>(named).Name);
        }
        finally
        {
            GlobalConfiguration.Configuration.UseDefaultTypeResolver();
        }
    }

    [Fact, CleanSerializerSettings]
    public void DeserializeInput_RefusesATypeName_InAValueOfTypeObject()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

        Assert.Throws<JsonException>(() => SerializationHelper.DeserializeInput(
            "{\"shape\":{\"$type\":\"circle\",\"r\":1}}", typeof(Dictionary<string, object>)));
    }

    [Fact, CleanSerializerSettings, GlobalLock(Reason = "Changes the type serializer and resolver")]
    public void DeserializeInput_ReadsAnInterfaceValue_WhoseTypeIsNamedByACustomTypeSerializer()
    {
        List<string> resolved = [];
        try
        {
            GlobalConfiguration.Configuration
                .UseTypeSerializer(static x => x == typeof(Named) ? "named" : TypeHelper.DefaultTypeSerializer(x))
                .UseTypeResolver(x =>
                {
                    resolved.Add(x);
                    return x == "named" ? typeof(Named) : TypeHelper.DefaultTypeResolver(x);
                });

            string input = SerializationHelper.SerializeInput(new Named { Name = "n" }, typeof(ILabelled))!;
            object? named = SerializationHelper.DeserializeInput(input, typeof(ILabelled));

            Assert.StartsWith("{\"$type\":\"named\"", input, StringComparison.Ordinal);
            Assert.Equal("n", Assert.IsType<Named>(named).Name);
            Assert.Throws<JsonException>(() => SerializationHelper.DeserializeInput(
                "{\"$type\":\"unknown\",\"Name\":\"n\"}", typeof(ILabelled)));
            Assert.Equal(["named"], resolved);
        }
        finally
        {
            GlobalConfiguration.Configuration.UseDefaultTypeSerializer().UseDefaultTypeResolver();
        }
    }

    private static object RoundTripAsObject(object value)
    {
        string json = SerializationHelper.Serialize(value, typeof(object), SerializationOption.User);
        return SerializationHelper.Deserialize(json, typeof(object), SerializationOption.User)!;
    }

    private static void Configure(bool recommended)
    {
        if (recommended)
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        }
    }

    public sealed class Gadget
    {
        public static int Constructed;

        public Gadget()
        {
            Constructed++;
        }

        public string? Command { get; set; }
    }

    public static class FormRelay
    {
        // ReSharper disable once UnusedParameter.Global
        public static void Handle(object form)
        {
        }
    }

    public interface ILabelled
    {
    }

    public sealed class Labelled : ILabelled
    {
        [JsonPropertyName("$type")]
        public string? TypeName { get; set; }

        [JsonPropertyName("$values")]
        public string? Values { get; set; }
    }

    public sealed class Named : ILabelled
    {
        public string? Name { get; set; }
    }

    public readonly struct Temperature
    {
        public Temperature(double celsius)
        {
            Celsius = celsius;
        }

        public Temperature(double celsius, string scale)
        {
            Celsius = celsius;
            Scale = scale;
        }

        public double Celsius { get; }
        public string? Scale { get; }
    }

    public readonly struct ComputedOnly
    {
        public bool IsMarker => true;
    }

    public readonly struct Money(decimal amount, string currency)
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency;
        public bool IsZero => Amount == 0;
    }

    [method: JsonConstructor]
    public readonly struct Bound(int value)
    {
        public int Value { get; } = value;
    }

    public struct Mutable
    {
        public int Value { get; set; }
    }

    [System.ComponentModel.TypeConverter(typeof(ConvertedConverter))]
    public sealed class Converted
    {
        public string? Name { get; set; }
    }

    public sealed class ConvertedConverter : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

        public override bool CanConvertTo(System.ComponentModel.ITypeDescriptorContext? context, Type? destinationType) => destinationType == typeof(string);

        public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value) => new Converted { Name = (string)value };

        public override object? ConvertTo(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object? value, Type destinationType) => ((Converted?)value)?.Name;
    }

    public static class Sink
    {
        // ReSharper disable UnusedParameter.Global
        public static void Pay(BigInteger amount)
        {
        }

        public static void Paint(Color named, Color custom)
        {
        }

        public static void Slice(Index index, Range range, Rune rune)
        {
        }

        public static void At(TimeZoneInfo utc, TimeZoneInfo custom)
        {
        }

        public static void Measure(Temperature temperature)
        {
        }

        public static void Press(ConsoleKeyInfo key)
        {
        }

        public static void Mark(ComputedOnly marker, Temperature zero)
        {
        }

        public static void Keep(Money money, Complex complex, KeyValuePair<string, int> pair, (int, string) tuple, Point point, DateOnly date, Bound bound, Mutable mutable, int? nullable)
        {
        }
        // ReSharper restore UnusedParameter.Global
    }
}
