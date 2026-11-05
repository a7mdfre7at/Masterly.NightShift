using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class JobTagsFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();
        private readonly List<(string Key, string Value, double Score)> _setAdds = new List<(string, string, double)>();

        public JobTagsFacts()
        {
            JobTags.Clock = () => _now;

            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetJobParameter("job-1", It.IsAny<string>())).Returns<string, string>((_, name) => _parameters.TryGetValue(name, out var value) ? value : null);
            _connection.Setup(x => x.SetJobParameter("job-1", It.IsAny<string>(), It.IsAny<string>())).Callback<string, string, string>((_, name, value) => _parameters[name] = value);
            _transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>())).Callback<string, string, double>((key, value, score) => _setAdds.Add((key, value, score)));
            _transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((key, value) => _setAdds.Add((key, value, 0)));
        }

        public void Dispose()
        {
            JobTags.Clock = () => DateTime.UtcNow;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Import(string customer, int rows) { }

        [Fact]
        public void Normalize_TrimsLowerCasesAndCollapsesWhitespace()
        {
            Assert.Equal("customer: contoso", JobTags.Normalize("  Customer:   Contoso "));
            Assert.Throws<ArgumentException>(() => JobTags.Normalize("   "));
            Assert.Throws<ArgumentException>(() => JobTags.Normalize(new string('x', 101)));
        }

        [Fact]
        public void TagAttribute_ResolvesTemplates_FromTheArguments()
        {
            var job = Job.FromExpression(() => Import("Contoso", 3));

            Assert.Equal("customer:contoso", new TagAttribute("customer:{0}").GetTag(job));
            Assert.Equal("import", new TagAttribute("import").GetTag(job));
            Assert.Throws<ArgumentException>(() => new TagAttribute(" "));
        }

        [Fact]
        public void TagAttribute_OnCreating_AccumulatesTags_InTheParameter()
        {
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import("Contoso", 3)) };
            var creating = context.GetCreatingContext();

            new TagAttribute("import").OnCreating(creating);
            new TagAttribute("customer:{0}").OnCreating(creating);
            new TagAttribute("import").OnCreating(creating);

            var tags = creating.GetJobParameter<string[]>(JobTags.ParameterName);
            Assert.Equal(new[] { "customer:contoso", "import" }, tags);
        }

        [Fact]
        public void TagAttribute_OnCreated_IndexesTheJob_UnlessCreationFailed()
        {
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import("Contoso", 3)) };
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            var attribute = new TagAttribute("customer:{0}");

            attribute.OnCreated(context.GetCreatedContext("job-1", canceled: true));
            attribute.OnCreated(context.GetCreatedContext("job-1", exception: new InvalidOperationException()));
            Assert.Empty(_setAdds);

            attribute.OnCreated(context.GetCreatedContext("job-1"));

            Assert.Contains(("tags", "customer:contoso", 0d), _setAdds);
            Assert.Contains(("tags:customer:contoso", "job-1", (double)JobHelper.ToTimestamp(_now)), _setAdds);
            _transaction.Verify(x => x.Commit(), Times.Once);
        }

        [Fact]
        public void Add_MergesWithExistingTags_AndIndexesOnlyTheNewOnes()
        {
            _parameters[JobTags.ParameterName] = "[\"import\"]";

            JobTags.Add(_connection.Object, "job-1", new[] { "Import", "region:EU" });

            Assert.Equal("[\"import\",\"region:eu\"]", _parameters[JobTags.ParameterName]);
            Assert.Single(_setAdds, x => x.Key == "tags:region:eu" && x.Value == "job-1");
            Assert.DoesNotContain(_setAdds, x => x.Key == "tags:import");
        }

        [Fact]
        public void Remove_UpdatesTheParameter_AndTheSet()
        {
            _parameters[JobTags.ParameterName] = "[\"import\",\"region:eu\"]";

            JobTags.Remove(_connection.Object, "job-1", "region:eu");

            Assert.Equal("[\"import\"]", _parameters[JobTags.ParameterName]);
            _transaction.Verify(x => x.RemoveFromSet("tags:region:eu", "job-1"));
        }

        [Fact]
        public void GetAll_CountsEveryTag_MostUsedFirst()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "a", "b", "c" });
            _connection.Setup(x => x.GetSetCount("tags:a")).Returns(2);
            _connection.Setup(x => x.GetSetCount("tags:b")).Returns(9);
            _connection.Setup(x => x.GetSetCount("tags:c")).Returns(2);

            var all = JobTags.GetAll(_connection.Object);

            Assert.Equal(new[] { "b", "a", "c" }, all.Select(x => x.Tag));
            Assert.Equal(9, all[0].Count);
        }

        [Fact]
        public void GetJobIds_ReadsFromTheEndOfTheSet_NewestFirst()
        {
            _connection.Setup(x => x.GetSetCount("tags:a")).Returns(10);
            _connection.Setup(x => x.GetRangeFromSet("tags:a", 5, 7)).Returns(new List<string> { "6", "7", "8" });

            var ids = JobTags.GetJobIds(_connection.Object, "A", from: 2, count: 3);

            Assert.Equal(new[] { "8", "7", "6" }, ids);
        }

        [Fact]
        public void GetJobIds_ReturnsNothing_PastTheEnd()
        {
            _connection.Setup(x => x.GetSetCount("tags:a")).Returns(3);

            Assert.Empty(JobTags.GetJobIds(_connection.Object, "a", from: 3, count: 10));
        }

        [Fact]
        public void Forget_RemovesTheGivenIds()
        {
            JobTags.Forget(_connection.Object, "a", new[] { "1", "2" });

            _transaction.Verify(x => x.RemoveFromSet("tags:a", "1"));
            _transaction.Verify(x => x.RemoveFromSet("tags:a", "2"));
            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void Delete_RemovesTheSet_AndTheIndexEntry()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);

            JobTags.Delete(_connection.Object, "A");

            storageTransaction.Verify(x => x.RemoveSet("tags:a"));
            storageTransaction.Verify(x => x.RemoveFromSet("tags", "a"));
            storageTransaction.Verify(x => x.Commit());
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void Add_WithoutConnection_TagsTheJob_InTheCurrentStorage_AndDisposesTheConnection()
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            JobStorage.Current = storage.Object;

            JobTags.Add("job-1", "Import", "region:EU");

            Assert.Equal("[\"import\",\"region:eu\"]", _parameters[JobTags.ParameterName]);
            Assert.Contains(("tags:import", "job-1", (double)JobHelper.ToTimestamp(_now)), _setAdds);
            Assert.Contains(("tags:region:eu", "job-1", (double)JobHelper.ToTimestamp(_now)), _setAdds);
            _connection.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void Add_ThrowsAnException_WhenArgumentsAreNull()
        {
            Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => JobTags.Add(null, "job-1", new[] { "a" })).ParamName);
            Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(() => JobTags.Add(_connection.Object, null, new[] { "a" })).ParamName);
            Assert.Equal("tags", Assert.Throws<ArgumentNullException>(() => JobTags.Add(_connection.Object, "job-1", (IEnumerable<string>)null)).ParamName);
        }

        [Fact]
        public void Add_DoesNothing_WhenNoTagIsGiven()
        {
            JobTags.Add(_connection.Object, "job-1", Array.Empty<string>());

            _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Add_DoesNotWrite_WhenTheJobAlreadyHasEveryTag()
        {
            _parameters[JobTags.ParameterName] = "[\"import\"]";

            JobTags.Add(_connection.Object, "job-1", new[] { "IMPORT" });

            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.Empty(_setAdds);
        }

        [Fact]
        public void GetTags_ReadsTheTagsOfAJob()
        {
            _parameters[JobTags.ParameterName] = "[\"import\",\"region:eu\"]";

            var tags = JobTags.GetTags(_connection.Object, "job-1");

            Assert.Equal(new[] { "import", "region:eu" }, tags);
        }

        [Fact]
        public void GetTags_ReturnsAnEmptyList_WhenTheJobHasNoTags()
        {
            var tags = JobTags.GetTags(_connection.Object, "job-1");

            Assert.Empty(tags);
        }

        [Fact]
        public void GetTags_ThrowsAnException_WhenArgumentsAreNull()
        {
            Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => JobTags.GetTags(null, "job-1")).ParamName);
            Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(() => JobTags.GetTags(_connection.Object, null)).ParamName);
        }

        [Fact]
        public void GetAll_CountsSetMembers_OnALegacyConnection()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "a", "b" });
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1" });
            connection.Setup(x => x.GetAllItemsFromSet("tags:b")).Returns(new HashSet<string> { "1", "2", "3" });

            var all = JobTags.GetAll(connection.Object);

            Assert.Equal(new[] { "b", "a" }, all.Select(x => x.Tag));
            Assert.Equal(new long[] { 3, 1 }, all.Select(x => x.Count));
        }

        [Fact]
        public void Count_UsesTheSetCount_OnAStorageConnection()
        {
            _connection.Setup(x => x.GetSetCount("tags:region:eu")).Returns(7);

            var count = JobTags.Count(_connection.Object, " Region:EU ");

            Assert.Equal(7, count);
        }

        [Fact]
        public void Count_CountsTheSetMembers_OnALegacyConnection()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1", "2" });

            var count = JobTags.Count(connection.Object, "A");

            Assert.Equal(2, count);
        }

        [Fact]
        public void Count_ReturnsZero_WhenALegacyConnectionHasNoSet()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns((HashSet<string>)null);

            var count = JobTags.Count(connection.Object, "a");

            Assert.Equal(0, count);
        }

        [Fact]
        public void Count_ThrowsAnException_WhenConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => JobTags.Count(null, "a"));

            Assert.Equal("connection", exception.ParamName);
        }

        [Fact]
        public void GetJobIds_OnALegacyConnection_ReturnsAPage_NewestFirst()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1", "2", "3", "4", "5" });

            var ids = JobTags.GetJobIds(connection.Object, "a", from: 1, count: 2);

            Assert.Equal(new[] { "4", "3" }, ids);
        }

        [Fact]
        public void GetJobIds_OnALegacyConnection_ReturnsNothing_WhenTheSetIsMissing()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns((HashSet<string>)null);

            Assert.Empty(JobTags.GetJobIds(connection.Object, "a", from: 0, count: 10));
        }

        [Fact]
        public void GetJobIds_ValidatesTheRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => JobTags.GetJobIds(_connection.Object, "a", from: -1, count: 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => JobTags.GetJobIds(_connection.Object, "a", from: 0, count: 0));
        }

        [Fact]
        public void Delete_WithALegacyTransaction_RemovesEveryMember_AndTheIndexEntry()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1", "2" });

            JobTags.Delete(_connection.Object, "A");

            _transaction.Verify(x => x.RemoveFromSet("tags:a", "1"), Times.Once);
            _transaction.Verify(x => x.RemoveFromSet("tags:a", "2"), Times.Once);
            _transaction.Verify(x => x.RemoveFromSet("tags", "a"), Times.Once);
            _transaction.Verify(x => x.Commit(), Times.Once);
        }

        [Fact]
        public void Delete_WithALegacyTransaction_RemovesOnlyTheIndexEntry_WhenTheSetIsMissing()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns((HashSet<string>)null);

            JobTags.Delete(_connection.Object, "a");

            _transaction.Verify(x => x.RemoveFromSet("tags:a", It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.RemoveFromSet("tags", "a"), Times.Once);
            _transaction.Verify(x => x.Commit(), Times.Once);
        }

        [Fact]
        public void Parse_ToleratesGarbage()
        {
            Assert.Empty(JobTags.Parse(null));
            Assert.Empty(JobTags.Parse("not json"));
            Assert.Equal(new[] { "x" }, JobTags.Parse("[\"x\"]"));
        }
    }
}
