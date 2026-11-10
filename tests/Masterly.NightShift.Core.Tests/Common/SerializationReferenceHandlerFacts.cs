using System.Text.Json;
using System.Text.Json.Serialization;

namespace Masterly.NightShift.Core.Tests.Common;

/// <summary>
/// User options with a <see cref="ReferenceHandler"/>: object graphs with cycles or shared references through
/// collection members are written exactly as System.Text.Json writes them, and read back the same way.
/// </summary>
public class SerializationReferenceHandlerFacts
{
    // What System.Text.Json writes for a root with one child that points back at it, with Preserve
    private const string PreservedTree =
        "{\"$id\":\"1\",\"Name\":\"root\",\"Parent\":null,\"Children\":{\"$id\":\"2\",\"$values\":[{\"$id\":\"3\",\"Name\":\"child\",\"Parent\":{\"$ref\":\"1\"},\"Children\":{\"$id\":\"4\",\"$values\":[]}}]}}";

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void IgnoreCycles_WritesACycleThroughAListMember_AsSystemTextJsonDoes_AndReadsItBack()
    {
        JsonSerializerOptions options = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };
        SerializationHelper.SetUserSerializerSettings(options);
        TreeNode root = CreateTree();

        string json = SerializationHelper.Serialize(root, SerializationOption.User);
        TreeNode? back = SerializationHelper.Deserialize<TreeNode>(json, SerializationOption.User);

        Assert.Equal(JsonSerializer.Serialize(root, options), json);
        Assert.Equal("root", back.Name);
        Assert.Equal("child", Assert.Single(back.Children).Name);
        Assert.Null(back.Children[0].Parent);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void Preserve_WritesACycleThroughAListMember_AsSystemTextJsonDoes_AndRestoresIt()
    {
        JsonSerializerOptions options = new() { ReferenceHandler = ReferenceHandler.Preserve };
        SerializationHelper.SetUserSerializerSettings(options);
        TreeNode root = CreateTree();

        string json = SerializationHelper.Serialize(root, SerializationOption.User);
        TreeNode? back = SerializationHelper.Deserialize<TreeNode>(json, SerializationOption.User);

        Assert.Equal(JsonSerializer.Serialize(root, options), json);
        Assert.Same(back, Assert.Single(back.Children).Parent);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void Preserve_ReadsTheJsonSystemTextJsonWrites_WithValuesAndReferences()
    {
        SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });

        TreeNode? back = SerializationHelper.Deserialize<TreeNode>(PreservedTree, SerializationOption.User);

        Assert.Equal("root", back.Name);
        Assert.Same(back, Assert.Single(back.Children).Parent);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void Preserve_KeepsASharedReference_ThroughAListMember_OfAJobArgument()
    {
        SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
        Line line = new() { Sku = "s" };
        Order order = new() { Name = "o", Main = line, Lines = { line } };
        Job job = Job.FromExpression(() => JobService.Process(order));

        string? payload = InvocationData.SerializeJob(job).SerializePayload();
        Order back = (Order)InvocationData.DeserializePayload(payload).DeserializeJob().Args[0];

        Assert.Same(back.Main, Assert.Single(back.Lines));
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void IgnoreCycles_LetsAJobArgument_HaveACycleThroughAListMember()
    {
        SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        Order order = new() { Name = "o" };
        order.Lines.Add(new Line { Sku = "a", Order = order });
        Job job = Job.FromExpression(() => JobService.Process(order));

        string? payload = InvocationData.SerializeJob(job).SerializePayload();
        Order back = (Order)InvocationData.DeserializePayload(payload).DeserializeJob().Args[0];

        Assert.Equal("a", Assert.Single(back.Lines).Sku);
        Assert.Null(back.Lines[0].Order);
    }

    [DataCompatibilityRangeFact, CleanSerializerSettings]
    public void IgnoreCyclesAndPreserve_HandleACycleThroughAnArrayMember_AsSystemTextJsonDoes()
    {
        foreach (ReferenceHandler? handler in new[] { ReferenceHandler.IgnoreCycles, ReferenceHandler.Preserve })
        {
            JsonSerializerOptions options = new() { ReferenceHandler = handler };
            SerializationHelper.SetUserSerializerSettings(options);
            Shipment shipment = new() { Name = "s" };
            shipment.Parcels = [new Parcel { Code = "p", Shipment = shipment }];

            string json = SerializationHelper.Serialize(shipment, SerializationOption.User);
            Shipment? back = SerializationHelper.Deserialize<Shipment>(json, SerializationOption.User);

            Assert.Equal(JsonSerializer.Serialize(shipment, options), json);
            Assert.Equal("p", Assert.Single(back.Parcels).Code);
        }
    }

    private static TreeNode CreateTree()
    {
        TreeNode root = new() { Name = "root" };
        root.Children.Add(new TreeNode { Name = "child", Parent = root });
        return root;
    }

    public static class JobService
    {
        // ReSharper disable once UnusedParameter.Global
        public static void Process(Order order)
        {
        }
    }

    public sealed class TreeNode
    {
        public string Name { get; set; }
        public TreeNode Parent { get; set; }
        public List<TreeNode> Children { get; set; } = [];
    }

    public sealed class Order
    {
        public string Name { get; set; }
        public List<Line> Lines { get; set; } = [];
        public Line Main { get; set; }
    }

    public sealed class Shipment
    {
        public string Name { get; set; }
        public Parcel[] Parcels { get; set; }
    }

    public sealed class Parcel
    {
        public string Code { get; set; }
        public Shipment Shipment { get; set; }
    }

    public sealed class Line
    {
        public string Sku { get; set; }
        public Order Order { get; set; }
    }
}
