using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
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
            var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles };
            SerializationHelper.SetUserSerializerSettings(options);
            var root = CreateTree();

            var json = SerializationHelper.Serialize(root, SerializationOption.User);
            var back = SerializationHelper.Deserialize<TreeNode>(json, SerializationOption.User);

            Assert.Equal(JsonSerializer.Serialize(root, options), json);
            Assert.Equal("root", back.Name);
            Assert.Equal("child", Assert.Single(back.Children).Name);
            Assert.Null(back.Children[0].Parent);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Preserve_WritesACycleThroughAListMember_AsSystemTextJsonDoes_AndRestoresIt()
        {
            var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
            SerializationHelper.SetUserSerializerSettings(options);
            var root = CreateTree();

            var json = SerializationHelper.Serialize(root, SerializationOption.User);
            var back = SerializationHelper.Deserialize<TreeNode>(json, SerializationOption.User);

            Assert.Equal(JsonSerializer.Serialize(root, options), json);
            Assert.Same(back, Assert.Single(back.Children).Parent);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Preserve_ReadsTheJsonSystemTextJsonWrites_WithValuesAndReferences()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });

            var back = SerializationHelper.Deserialize<TreeNode>(PreservedTree, SerializationOption.User);

            Assert.Equal("root", back.Name);
            Assert.Same(back, Assert.Single(back.Children).Parent);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Preserve_KeepsASharedReference_ThroughAListMember_OfAJobArgument()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
            var line = new Line { Sku = "s" };
            var order = new Order { Name = "o", Main = line, Lines = { line } };
            var job = Job.FromExpression(() => JobService.Process(order));

            var payload = InvocationData.SerializeJob(job).SerializePayload();
            var back = (Order)InvocationData.DeserializePayload(payload).DeserializeJob().Args[0];

            Assert.Same(back.Main, Assert.Single(back.Lines));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void IgnoreCycles_LetsAJobArgument_HaveACycleThroughAListMember()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
            var order = new Order { Name = "o" };
            order.Lines.Add(new Line { Sku = "a", Order = order });
            var job = Job.FromExpression(() => JobService.Process(order));

            var payload = InvocationData.SerializeJob(job).SerializePayload();
            var back = (Order)InvocationData.DeserializePayload(payload).DeserializeJob().Args[0];

            Assert.Equal("a", Assert.Single(back.Lines).Sku);
            Assert.Null(back.Lines[0].Order);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void IgnoreCyclesAndPreserve_HandleACycleThroughAnArrayMember_AsSystemTextJsonDoes()
        {
            foreach (var handler in new[] { ReferenceHandler.IgnoreCycles, ReferenceHandler.Preserve })
            {
                var options = new JsonSerializerOptions { ReferenceHandler = handler };
                SerializationHelper.SetUserSerializerSettings(options);
                var shipment = new Shipment { Name = "s" };
                shipment.Parcels = new[] { new Parcel { Code = "p", Shipment = shipment } };

                var json = SerializationHelper.Serialize(shipment, SerializationOption.User);
                var back = SerializationHelper.Deserialize<Shipment>(json, SerializationOption.User);

                Assert.Equal(JsonSerializer.Serialize(shipment, options), json);
                Assert.Equal("p", Assert.Single(back.Parcels).Code);
            }
        }

        private static TreeNode CreateTree()
        {
            var root = new TreeNode { Name = "root" };
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
            public List<TreeNode> Children { get; set; } = new List<TreeNode>();
        }

        public sealed class Order
        {
            public string Name { get; set; }
            public List<Line> Lines { get; set; } = new List<Line>();
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
}
