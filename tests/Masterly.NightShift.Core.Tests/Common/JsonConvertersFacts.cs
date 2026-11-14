using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static object RoundTripAsObject(object value)
    {
        string json = SerializationHelper.Serialize(value, typeof(object), SerializationOption.User);
        return SerializationHelper.Deserialize(json, typeof(object), SerializationOption.User)!;
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
}
