using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    /// <summary>
    /// Arguments written by Hangfire 1.8.25 (Newtonsoft.Json) with its recommended settings, which keep type names
    /// (<c>TypeNameHandling.Auto</c>), read back by Masterly.NightShift whatever its serializer settings.
    /// </summary>
    public class HangfirePayloadFacts
    {
        // Captured from Hangfire.Core 1.8.25: InvocationData.SerializeJob(...).Arguments, one element each
        private const string EnumerableOfIntHoldingArray =
            "{\"$type\":\"System.Int32[], System.Private.CoreLib\",\"$values\":[1,2]}";
        private const string ReadOnlyCollectionOfIntHoldingList =
            "{\"$type\":\"System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]], System.Private.CoreLib\",\"$values\":[1,2]}";
        private const string ReadOnlyListOfStringHoldingList =
            "{\"$type\":\"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib\",\"$values\":[\"a\",\"b\"]}";
        private const string ReadOnlyDictionaryHoldingDictionary =
            "{\"$type\":\"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]], System.Private.CoreLib\",\"k\":1}";
        private const string DictionaryHoldingSortedDictionary =
            "{\"$type\":\"System.Collections.Generic.SortedDictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Collections\",\"k\":\"v\"}";
        private const string DictionaryOfStringHoldingItself =
            "{\"$type\":\"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[System.String, System.Private.CoreLib]], System.Private.CoreLib\",\"m\":\"n\"}";
        private const string ObjectWithReadOnlyListMember =
            "{\"Name\":\"n\",\"Items\":{\"$type\":\"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib\",\"$values\":[\"i\"]}}";
        private const string ListOfReadOnlyLists =
            "[{\"$type\":\"System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]], System.Private.CoreLib\",\"$values\":[1]},{\"$type\":\"System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]], System.Private.CoreLib\",\"$values\":[2,3]}]";
        private const string GetOnlyCollections = "{\"Items\":[5],\"Map\":{\"a\":1}}";
        private const string MoneyJson = "{\"Amount\":1.5,\"Currency\":\"EUR\"}";

        public static TheoryData<string> Settings => new TheoryData<string> { "default", "recommended", "custom" };

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_ReadsAWrappedCollection_AsTheDeclaredInterface(string settings)
        {
            Configure(settings);

            Assert.Equal(new[] { 1, 2 }, Read<IEnumerable<int>>(EnumerableOfIntHoldingArray));
            Assert.Equal(new[] { 1, 2 }, Read<IReadOnlyCollection<int>>(ReadOnlyCollectionOfIntHoldingList));
            Assert.Equal(new[] { "a", "b" }, Read<IReadOnlyList<string>>(ReadOnlyListOfStringHoldingList));
            Assert.Equal(new[] { 1, 2 }, Read<ICollection<int>>(ReadOnlyCollectionOfIntHoldingList));
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_ReadsADictionary_StoredWithALeadingTypeName(string settings)
        {
            Configure(settings);

            var numbers = Read<IReadOnlyDictionary<string, int>>(ReadOnlyDictionaryHoldingDictionary);
            var texts = Read<IDictionary<string, string>>(DictionaryHoldingSortedDictionary);

            Assert.Equal(new[] { new KeyValuePair<string, int>("k", 1) }, numbers);
            Assert.Equal(new[] { new KeyValuePair<string, string>("k", "v") }, texts);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_ReadsWrappedCollections_NestedInAnObjectOrAList(string settings)
        {
            Configure(settings);

            var holder = Read<Holder>(ObjectWithReadOnlyListMember);
            var lists = Read<List<IReadOnlyList<int>>>(ListOfReadOnlyLists);

            Assert.Equal("n", holder.Name);
            Assert.Equal(new[] { "i" }, holder.Items);
            Assert.Equal(2, lists.Count);
            Assert.Equal(new[] { 1 }, lists[0]);
            Assert.Equal(new[] { 2, 3 }, lists[1]);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void DeserializeJob_ReadsAJob_WithAWrappedCollectionArgument(string settings)
        {
            Configure(settings);
            var method = typeof(JobService).GetMethod(nameof(JobService.Sum));

            var data = new InvocationData(
                typeof(JobService).AssemblyQualifiedName,
                nameof(JobService.Sum),
                SerializationHelper.Serialize(new[] { typeof(IReadOnlyCollection<int>) }),
                SerializationHelper.Serialize(new[] { ReadOnlyCollectionOfIntHoldingList }));

            var job = data.DeserializeJob();

            Assert.Equal(method, job.Method);
            Assert.Equal(new[] { 1, 2 }, (IEnumerable<int>)job.Args[0]);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_IgnoresTheTypeNameOfAWrappedCollection()
        {
            // Stored data can't make a server create the type it names, even one that fits the declared type
            var json = "{\"$type\":\"System.Collections.Generic.HashSet`1[[System.Int32, mscorlib]], System.Core\",\"$values\":[1,1]}";

            var result = Read<IEnumerable<int>>(json);

            Assert.IsType<List<int>>(result);
            Assert.Equal(new[] { 1, 1 }, result);
            Assert.Equal(new[] { 3 }, Read<IEnumerable<int>>("{\"$type\":\"Missing.Type, Missing\",\"$values\":[3]}"));
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Serialize_WritesCollectionsAndDictionaries_AsPlainJson(string settings)
        {
            Configure(settings);

            Assert.Equal("[1,2]", SerializationHelper.Serialize<IReadOnlyCollection<int>>(new List<int> { 1, 2 }, SerializationOption.User));
            Assert.Equal("{\"k\":1}", SerializationHelper.Serialize<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { ["k"] = 1 }, SerializationOption.User));
            Assert.Equal("[[1],[2,3]]", SerializationHelper.Serialize(new List<IReadOnlyList<int>> { new List<int> { 1 }, new List<int> { 2, 3 } }, SerializationOption.User));
            Assert.Equal("{\"Queue\":\"default\"}", SerializationHelper.Serialize(new Dictionary<string, string> { ["Queue"] = "default" }, SerializationOption.Internal));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_ReadsPlainCollectionsAndDictionaries_AsBefore()
        {
            Assert.Equal(new[] { 1, 2 }, Read<IReadOnlyList<int>>("[1,2]"));
            Assert.Equal(new[] { 1, 2 }, Read<HashSet<int>>("[1,2]").OrderBy(static x => x));
            Assert.Equal("v", Read<Dictionary<string, string>>("{\"k\":\"v\"}")["k"]);
            Assert.Equal("t", Read<Dictionary<string, string>>("{\"k\":\"v\",\"$type\":\"t\"}")["$type"]);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_KeepsALeadingTypeKey_ThatDoesNotNameAFittingDictionaryType(string settings)
        {
            Configure(settings);
            var map = new Dictionary<string, string> { ["$type"] = "user-value", ["a"] = "b" };
            var holder = new MapHolder { Map = new Dictionary<string, string> { ["$type"] = "System.String, mscorlib", ["c"] = "d" } };

            var mapBack = Read<Dictionary<string, string>>(SerializationHelper.Serialize(map, SerializationOption.User));
            var holderBack = Read<MapHolder>(SerializationHelper.Serialize(holder, SerializationOption.User));
            var listName = Read<IDictionary<string, string>>(
                "{\"$type\":\"System.Collections.Generic.List`1[[System.String, mscorlib]], mscorlib\",\"k\":\"v\"}");

            Assert.Equal(map, mapBack);
            Assert.Equal(holder.Map, holderBack.Map);
            Assert.Equal(2, listName.Count);
            Assert.Equal("v", listName["k"]);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_StillDropsTheLeadingTypeName_OfADictionaryMember_StoredByHangfire(string settings)
        {
            Configure(settings);

            // With TypeNameHandling.Objects a dictionary got its own type name; with Auto, the actual type's
            var holder = Read<MapHolder>("{\"Map\":" + DictionaryOfStringHoldingItself + ",\"Sorted\":" + DictionaryHoldingSortedDictionary + "}");

            Assert.Equal(new[] { new KeyValuePair<string, string>("m", "n") }, holder.Map);
            Assert.Equal(new[] { new KeyValuePair<string, string>("k", "v") }, holder.Sorted);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_AddsItems_ToACollectionWithoutSetter_ThatTheStructsConstructorCreates(string settings)
        {
            Configure(settings);

            var point = Read<TaggedPoint>("{\"X\":1.5,\"Y\":2,\"Tags\":[\"t1\",\"t2\"]}");

            Assert.Equal(1.5, point.X);
            Assert.Equal(2, point.Y);
            Assert.Equal(new[] { "t1", "t2" }, point.Tags);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_ReadsAStruct_WhoseMembersOnlyItsConstructorSets(string settings)
        {
            Configure(settings);

            var money = Read<Money>(MoneyJson);

            Assert.Equal(1.5m, money.Amount);
            Assert.Equal("EUR", money.Currency);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Serialize_AndDeserialize_RoundTripAStruct_WhoseMembersOnlyItsConstructorSets(string settings)
        {
            Configure(settings);
            var job = Job.FromExpression(() => JobService.Pay(new Money(2.25m, "usd"), new Tagged("x", 3) { Note = "n" }));

            var data = InvocationData.SerializeJob(job);
            var back = InvocationData.DeserializePayload(data.SerializePayload()).DeserializeJob();

            var money = Assert.IsType<Money>(back.Args[0]);
            var tagged = Assert.IsType<Tagged>(back.Args[1]);
            Assert.Equal(2.25m, money.Amount);
            Assert.Equal("USD", money.Currency);
            Assert.Equal("x", tagged.Name);
            Assert.Equal(3, tagged.Count);
            Assert.Equal("n", tagged.Note);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_CallsTheConstructor_WithDefaults_ForMembersMissingFromTheJson()
        {
            var money = Read<Money>("{\"Amount\":4}");
            var tagged = Read<Tagged>("{\"Name\":\"a\"}");
            var leveled = Read<Leveled>("{}");

            Assert.Equal(4m, money.Amount);
            Assert.Equal("NONE", money.Currency);
            Assert.Equal("a", tagged.Name);
            Assert.Equal(7, tagged.Count);
            Assert.Null(tagged.Note);
            Assert.Equal(DayOfWeek.Friday, leveled.Day);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_LeavesStructsAlone_ThatSystemTextJsonAlreadyReads()
        {
            Assert.Equal(5, Read<MarkedStruct>("{\"Value\":5}").Value);
            Assert.Equal(6, Read<SettableStruct>("{\"Value\":6}").Value);
            Assert.Equal(0, Read<TwoConstructors>("{\"Value\":7}").Value);
        }

        [DataCompatibilityRangeTheory, CleanSerializerSettings]
        [MemberData(nameof(Settings))]
        public void Deserialize_AddsItems_ToCollectionMembersWithoutSetter(string settings)
        {
            Configure(settings);

            var result = Read<GetOnly>(GetOnlyCollections);

            Assert.Equal(new[] { 1, 5 }, result.Items);
            Assert.Equal(1, result.Map["a"]);
            Assert.Equal(2, result.Map["b"]);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_AddsItems_ToACollectionDeclaredAsAReadOnlyInterface()
        {
            var result = Read<GetOnlyInterfaces>("{\"Items\":[\"x\"],\"Fixed\":[1],\"Missing\":[2]}");

            Assert.Equal(new[] { "x" }, result.Items);
            Assert.Equal(new[] { 9 }, result.Fixed);
            Assert.Null(result.Missing);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Deserialize_LeavesMembers_SystemTextJsonPopulatesItself()
        {
            var result = Read<PopulatedByOptions>("{\"Items\":[2]}");

            Assert.Equal(new[] { 1, 2 }, result.Items);
        }

        private static void Configure(string settings)
        {
            switch (settings)
            {
                case "recommended":
                    SerializationHelper.SetRecommendedSerializerSettings(null);
                    break;
                case "custom":
                    SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    break;
            }
        }

        private static T Read<T>(string json)
        {
            return SerializationHelper.Deserialize<T>(json, SerializationOption.User);
        }

        public static class JobService
        {
            // ReSharper disable UnusedMember.Global, UnusedParameter.Global
            public static int Sum(IReadOnlyCollection<int> values) => values.Sum();

            public static void Pay(Money money, Tagged tagged)
            {
            }
            // ReSharper restore UnusedMember.Global, UnusedParameter.Global
        }

        public sealed class MapHolder
        {
            public Dictionary<string, string> Map { get; set; }
            public IDictionary<string, string> Sorted { get; set; }
        }

        public readonly struct TaggedPoint
        {
            public TaggedPoint(double x, double y)
            {
                X = x;
                Y = y;
                Tags = new List<string>();
            }

            public double X { get; }
            public double Y { get; }
            public List<string> Tags { get; }
        }

        public sealed class Holder
        {
            public string Name { get; set; }
            public IReadOnlyList<string> Items { get; set; }
        }

        public readonly struct Money
        {
            public Money(decimal amount, string currency = "none")
            {
                Amount = amount;
                Currency = currency?.ToUpperInvariant();
            }

            public decimal Amount { get; }
            public string Currency { get; }
        }

        public struct Tagged
        {
            public Tagged(string name, int count = 7)
            {
                Name = name;
                Count = count;
                Note = null;
            }

            public string Name { get; }
            public int Count { get; }
            public string Note { get; set; }
        }

        public readonly struct Leveled
        {
            public Leveled(DayOfWeek day = DayOfWeek.Friday)
            {
                Day = day;
            }

            public DayOfWeek Day { get; }
        }

        public readonly struct MarkedStruct
        {
            [JsonConstructor]
            public MarkedStruct(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        public struct SettableStruct
        {
            public SettableStruct(int value)
            {
                Value = value;
            }

            public int Value { get; set; }
        }

        public readonly struct TwoConstructors
        {
            public TwoConstructors(int value)
            {
                Value = value;
            }

            public TwoConstructors(int value, int factor)
            {
                Value = value * factor;
            }

            public int Value { get; }
        }

        public sealed class GetOnly
        {
            public List<int> Items { get; } = new List<int> { 1 };
            public Dictionary<string, int> Map { get; } = new Dictionary<string, int> { ["a"] = 0, ["b"] = 2 };
        }

        public sealed class GetOnlyInterfaces
        {
            public IReadOnlyList<string> Items { get; } = new List<string>();
            public IList<int> Fixed { get; } = new ReadOnlyCollection<int>(new[] { 9 });
            public ICollection<int> Missing { get; } = null;
        }

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public sealed class PopulatedByOptions
        {
            public List<int> Items { get; } = new List<int> { 1 };
        }
    }
}
