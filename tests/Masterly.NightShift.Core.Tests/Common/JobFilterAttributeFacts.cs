namespace Masterly.NightShift.Core.Tests.Common;

public class JobFilterAttributeFacts
{
    [Fact]
    public void SetOrder_ThrowsAnException_WhenValueIsLessThanDefaultOrder()
    {
        Mock<JobFilterAttribute> filterAttribute = new() { CallBase = true };
        Assert.Throws<ArgumentOutOfRangeException>(
            () => filterAttribute.Object.Order = -2);
    }

    [Fact]
    public void TypeId_Property_IsNotIncludedIntoSerializedForm()
    {
        SampleJobAttribute attribute = new();
        string serialized = SerializationHelper.Serialize(attribute);
        Assert.DoesNotContain("TypeId", serialized);
    }

    [Fact]
    public void AllowMultiple_Property_IsNotIncludedIntoSerializedForm_SinceItIsGetOnlyProperty()
    {
        SampleJobAttribute attribute = new();
        string serialized = SerializationHelper.Serialize(attribute);
        Assert.DoesNotContain("AllowMultiple", serialized);
    }

    [Fact]
    public void Order_Property_IsNotIncludedIntoSerializedForm_WhenDefaultValueIsUsed()
    {
        SampleJobAttribute attribute = new();
        string serialized = SerializationHelper.Serialize(attribute);
        Assert.DoesNotContain("Order", serialized);
    }

    [Fact]
    public void Order_Property_IsIncludedIntoSerializedForm_WhenNonDefaultValueIsUsed()
    {
        SampleJobAttribute attribute = new() { Order = 555 };
        string serialized = SerializationHelper.Serialize(attribute);
        Assert.Contains("\"Order\":555", serialized);
    }

    [Fact]
    public void Order_Property_ProperlyHandlesDefaultValue_WhenBeingDeserialized()
    {
        SampleJobAttribute? attribute = SerializationHelper.Deserialize<SampleJobAttribute>("{}");
        Assert.Equal(-1, attribute.Order);
    }

    private sealed class SampleJobAttribute : JobFilterAttribute
    {
    }
}
