using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Masterly.NightShift.Annotations;


namespace Masterly.NightShift.Core.Tests.Common;

public class JobArgumentFacts
{
    private readonly Mock<JobActivator> _activator;

    public JobArgumentFacts()
    {
        _activator = new Mock<JobActivator>();
        _activator.Setup(x => x.ActivateJob(It.IsAny<Type>()))
            .Returns(() => new JobArgumentFacts());

    }

    private const bool BooleanValue = true;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(bool value) { Assert.Equal(BooleanValue, value); }

    [Fact]
    public void BooleanArguments_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(BooleanValue);
    }

    private const byte ByteValue = 142;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(byte value) { Assert.Equal(ByteValue, value); }

    [Fact]
    public void ByteValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(ByteValue);
    }

    private const sbyte SByteValue = -111;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(sbyte value) { Assert.Equal(SByteValue, value); }

    [Fact]
    public void SByteValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(SByteValue);
    }

    private const char CharValue = char.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(char value) { Assert.Equal(CharValue, value); }

    [Fact]
    public void CharValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(CharValue);
    }

    private const decimal DecimalValue = decimal.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(decimal value) { Assert.Equal(DecimalValue, value); }

    [Fact]
    public void DecimalValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(DecimalValue);
    }

    private const double DoubleValue = 3.14159265359D;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(double value) { Assert.Equal(DoubleValue, value); }

    [Fact]
    public void DoubleValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(DoubleValue);
    }

    private const float SingleValue = 3.1415F;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(float value) { Assert.Equal(SingleValue, value); }

    [Fact]
    public void SingleValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(SingleValue);
    }

    private const int Int32Value = int.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(int value) { Assert.Equal(Int32Value, value); }

    [Fact]
    public void Int32Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(Int32Value);
    }

    private const uint UInt32Value = uint.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(uint value) { Assert.Equal(UInt32Value, value); }

    [Fact]
    public void UInt32Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(UInt32Value);
    }

    private const long Int64Value = long.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(long value) { Assert.Equal(Int64Value, value); }

    [Fact]
    public void Int64Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(Int64Value);
    }

    private const ulong UInt64Value = ulong.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(ulong value) { Assert.Equal(UInt64Value, value); }

    [Fact]
    public void UInt64Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(UInt64Value);
    }

    private const short Int16Value = short.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(short value) { Assert.Equal(Int16Value, value); }

    [Fact]
    public void Int16Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(Int16Value);
    }

    private const ushort UInt16Value = ushort.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(ushort value) { Assert.Equal(UInt16Value, value); }

    [Fact]
    public void UInt16Values_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(UInt16Value);
    }

    private const string StringValue = "jkashdgfa$%^&";

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(string value) { Assert.Equal(StringValue, value); }

    [Fact]
    public void StringValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(StringValue);
    }

    private static readonly TimeSpan TimeSpanValue = TimeSpan.FromDays(1);

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(TimeSpan value) { Assert.Equal(TimeSpanValue, value); }

    [Fact]
    public void TimeSpanValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(TimeSpanValue);
    }

    private static readonly object ObjectValue = "Hellojkadg";

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(object value) { Assert.Equal(ObjectValue, value); }

    [Fact]
    public void ObjectValues_AreBeingDeserializedAsStrings()
    {
        CreateAndPerform(ObjectValue);
    }

    private static readonly DateTimeOffset DateTimeOffsetValue = new(new DateTime(2012, 12, 12), TimeSpan.FromHours(1));

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(DateTimeOffset value) { Assert.Equal(DateTimeOffsetValue, value); }

    [Fact]
    public void DateTimeOffsetValues_AreBeingDeserializedCorrectly()
    {
        // Don't run this test on Mono – https://bugzilla.xamarin.com/show_bug.cgi?id=25158
        if (Type.GetType("Mono.Runtime") == null)
        {
            CreateAndPerform(DateTimeOffsetValue);
        }
    }

    private static readonly CultureInfo CultureInfoValue = new("ru-RU");

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(CultureInfo value) { Assert.Equal(CultureInfoValue, value); }

    [Fact]
    public void CultureInfoValues_AreBeingDeserializedCorrectly()
    {
        CreateAndPerform(CultureInfoValue);
    }

    private const DayOfWeek EnumValue = DayOfWeek.Saturday;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(DayOfWeek value) { Assert.Equal(EnumValue, value); }

    [Fact]
    public void EnumValues_AreBeingDeserializedCorrectly()
    {
        CreateAndPerform(EnumValue);
    }

    private static readonly Guid GuidValue = Guid.NewGuid();

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(Guid value) { Assert.Equal(GuidValue, value); }

    [Fact]
    public void GuidValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(GuidValue);
    }

    private static readonly Uri UriValue = new("https://example.com", UriKind.Absolute);

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(Uri value) { Assert.Equal(UriValue, value); }

    [Fact]
    public void UriValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(UriValue);
    }

    private static readonly long? NotNullNullableValue = long.MaxValue;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(long? value) { Assert.Equal(NotNullNullableValue, value); }

    [Fact]
    public void NotNullNullableValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(NotNullNullableValue);
    }

    private static readonly int? NullNullableValue = null;

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(int? value) { Assert.Equal(NullNullableValue, value); }

    [Fact]
    public void NullNullableValues_AreBeingCorrectlyDeserialized()
    {
        CreateAndPerform(NullNullableValue);
    }

    private static readonly string[] ArrayValue = ["Hello", "world"];

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(string[] value) { Assert.Equal(ArrayValue, value); }

    [Fact]
    public void ArrayValues_AreBeingCorrectlyDeserialized_FromJson()
    {
        CreateAndPerform(ArrayValue, true);
    }

    private static readonly List<DateTime> ListValue = [DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)];

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(List<DateTime> value) { Assert.Equal(ListValue, value); }

    [Fact]
    public void ListValues_AreBeingCorrectlyDeserialized_FromJson()
    {
        CreateAndPerform(ListValue, true);
    }

    private static readonly Dictionary<TimeSpan, string> DictionaryValue = new()
        {
            { TimeSpan.FromSeconds(1), "123" },
            { TimeSpan.FromDays(12), "376" }
        };

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(Dictionary<TimeSpan, string> value) { Assert.Equal(DictionaryValue, value); }

    [Fact]
    public void DictionaryValues_AreBeingCorrectlyDeserialized_FromJson()
    {
        CreateAndPerform(DictionaryValue, true);
    }

    public struct MyStruct
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }

    private static readonly MyStruct CustomStructValue = new() { Id = Guid.NewGuid(), Name = "Masterly.NightShift" };

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(MyStruct value) { Assert.Equal(CustomStructValue, value); }

    [Fact]
    public void CustomStructValues_AreBeingCorrectlyDeserialized_FromJson()
    {
        CreateAndPerform(CustomStructValue, true);
    }

    public class MyClass : IEquatable<MyClass>
    {
        public DateTime CreatedAt { get; set; }

        public bool Equals(MyClass other)
        {
            if (other == null)
            {
                return false;
            }

            return CreatedAt.Equals(other.CreatedAt);
        }

        public override bool Equals(object obj)
        {
            return obj?.GetType() == GetType() && Equals((MyClass)obj);
        }

        public override int GetHashCode()
        {
            return CreatedAt.GetHashCode();
        }
    }

    private static readonly MyClass CustomClassValue = new() { CreatedAt = DateTime.UtcNow };

    [UsedImplicitly]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void Method(MyClass value) { Assert.Equal(CustomClassValue.CreatedAt, value.CreatedAt); }

    [Fact]
    public void CustomClassValues_AreBeingCorrectlyDeserialized_FromJson()
    {
        CreateAndPerform(CustomClassValue, true);
    }

    private static void CreateAndPerform<T>(T argumentValue, bool checkJsonOnly = false)
    {
        Type type = typeof(JobArgumentFacts);
        MethodInfo? methodInfo = type.GetMethod("Method", [typeof(T)]);

        List<Tuple<string, Func<string>>> serializationMethods = [];

        if (!checkJsonOnly)
        {
            TypeConverter converter = TypeDescriptor.GetConverter(typeof(T));
            serializationMethods.Add(new Tuple<string, Func<string>>(
                "TypeDescriptor",
                () => converter.ConvertToInvariantString(argumentValue)));
        }

        serializationMethods.Add(new Tuple<string, Func<string>>(
            "JSON",
            () => SerializationHelper.Serialize(argumentValue, SerializationOption.User)));

        foreach (Tuple<string, Func<string>> method in serializationMethods)
        {
            InvocationData data = new(
                methodInfo?.DeclaringType?.AssemblyQualifiedName,
                methodInfo?.Name,
                TestJson.ToJson(methodInfo?.GetParameters().Select(x => x.ParameterType).ToArray()),
                TestJson.ToJson(new[] { method.Item2() }));

            Job job = data.DeserializeJob();

            Assert.Equal(argumentValue, job.Args[0]);
        }
    }
}
