using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
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
        public void Add_AndRemove_ReadAndWriteTheTags_UnderALockOfTheirOwn()
        {
            var calls = new List<string>();
            var handle = new Mock<IDisposable>();
            handle.Setup(x => x.Dispose()).Callback(() => calls.Add("release"));
            _connection.Setup(x => x.AcquireDistributedLock("job:job-1:tags-lock", JobTags.LockTimeout)).Callback(() => calls.Add("lock")).Returns(handle.Object);
            _connection.Setup(x => x.GetJobParameter("job-1", JobTags.ParameterName)).Callback(() => calls.Add("read")).Returns(() => _parameters.TryGetValue(JobTags.ParameterName, out var value) ? value : null);
            _connection.Setup(x => x.SetJobParameter("job-1", JobTags.ParameterName, It.IsAny<string>())).Callback<string, string, string>((_, name, value) =>
            {
                calls.Add("write");
                _parameters[name] = value;
            });
            _transaction.Setup(x => x.Commit()).Callback(() => calls.Add("commit"));

            JobTags.Add(_connection.Object, "job-1", new[] { "a" });
            JobTags.Remove(_connection.Object, "job-1", "a");

            Assert.Equal(new[] { "lock", "read", "write", "commit", "release", "lock", "read", "write", "commit", "release" }, calls);
        }

        [Fact]
        public void Add_Throws_AndWritesNothing_WhenTheLockCanNotBeTaken()
        {
            _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Throws(new DistributedLockTimeoutException("job:job-1:tags-lock"));

            Assert.Throws<DistributedLockTimeoutException>(() => JobTags.Add(_connection.Object, "job-1", new[] { "a" }));

            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Normalize_TrimsLowerCasesAndCollapsesWhitespace()
        {
            Assert.Equal("customer: contoso", JobTags.Normalize("  Customer:   Contoso "));
            Assert.Throws<ArgumentException>(() => JobTags.Normalize("   "));
            Assert.Equal(JobTags.MaxLength, JobTags.Normalize(new string('x', JobTags.MaxLength)).Length);
            Assert.Throws<ArgumentException>(() => JobTags.Normalize(new string('x', JobTags.MaxLength + 1)));
        }

        [Fact]
        public void MaxLength_LeavesRoomForTheTagsPrefix_InA100CharacterKey()
        {
            // SQL Server keys are nvarchar(100): a longer key made the job exist while its creation threw
            Assert.Equal(100, JobTags.GetKey(new string('x', JobTags.MaxLength)).Length);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TagAttribute_GetTag_ReturnsNull_WhenTheArgumentLeavesNothing(string customer)
        {
            var job = Job.FromExpression(() => Import(customer, 3));

            Assert.Null(new TagAttribute("{0}").GetTag(job));
            Assert.Equal("customer:", new TagAttribute("customer:{0}").GetTag(job));
        }

        [Fact]
        public void TagAttribute_GetTag_ShortensLongValues_IntoDistinctStableTags()
        {
            var first = new TagAttribute("{0}").GetTag(Job.FromExpression(() => Import(new string('a', 150) + "1", 3)));
            var again = new TagAttribute("{0}").GetTag(Job.FromExpression(() => Import(new string('A', 150) + "1", 3)));
            var second = new TagAttribute("{0}").GetTag(Job.FromExpression(() => Import(new string('a', 150) + "2", 3)));

            Assert.Equal(JobTags.MaxLength, first.Length);
            Assert.StartsWith(new string('a', JobTags.MaxLength - 17) + "~", first, StringComparison.Ordinal);
            Assert.Matches("~[0-9a-f]{16}$", first);
            Assert.Equal(first, again);
            Assert.NotEqual(first, second);
            Assert.Equal(first, JobTags.Normalize(first));
        }

        [Fact]
        public void TagAttribute_GetTag_DoesNotSplitASurrogatePair_WhenShortening()
        {
            // The high surrogate lands on the last character of the readable prefix
            var value = new string('a', JobTags.MaxLength - 18) + "\U0001F600" + new string('b', 40);

            var tag = new TagAttribute("{0}").GetTag(Job.FromExpression(() => Import(value, 3)));

            Assert.False(Char.IsHighSurrogate(tag[tag.IndexOf('~') - 1]));
            Assert.True(tag.Length <= JobTags.MaxLength);
        }

        [Fact]
        public void TagAttribute_SkipsATagThatComesOutEmpty_InsteadOfFailingCreation()
        {
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import(null, 3)) };
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            var creating = context.GetCreatingContext();
            var attribute = new TagAttribute("{0}");

            attribute.OnCreating(creating);
            attribute.OnCreated(context.GetCreatedContext("job-1"));

            Assert.False(creating.Parameters.ContainsKey(JobTags.ParameterName));
            Assert.Empty(_setAdds);
            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void TagAttribute_IndexesAShortenedTag_ForALongArgument()
        {
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import(new string('c', 300), 3)) };
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            var creating = context.GetCreatingContext();
            var attribute = new TagAttribute("customer:{0}");

            attribute.OnCreating(creating);
            attribute.OnCreated(context.GetCreatedContext("job-1"));

            var tag = Assert.Single(creating.GetJobParameter<string[]>(JobTags.ParameterName));
            Assert.Equal(JobTags.MaxLength, tag.Length);
            Assert.Contains((JobTags.GetKey(tag), "job-1", (double)JobHelper.ToTimestamp(_now)), _setAdds);
        }

        [Fact]
        public void Add_StillRejectsEmptyAndOverlongTags()
        {
            Assert.Throws<ArgumentException>(() => JobTags.Add(_connection.Object, "job-1", new[] { " " }));
            Assert.Throws<ArgumentException>(() => JobTags.Add(_connection.Object, "job-1", new[] { new string('x', JobTags.MaxLength + 1) }));

            _connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Lookups_AcceptTagsLongerThanTheLimit()
        {
            var tag = new string('x', JobTags.MaxLength + 5);
            _connection.Setup(x => x.GetSetCount(JobTags.GetKey(tag))).Returns(1);

            Assert.Equal(1, JobTags.Count(_connection.Object, tag));
            Assert.Empty(JobTags.GetJobIds(_connection.Object, tag, 0, 10));
            Assert.Throws<ArgumentException>(() => JobTags.Count(_connection.Object, " "));
        }

        [Fact]
        public void GetNames_ListsTheTagNamesInOrder_WithoutCountingThem()
        {
            _connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "b", "c", "a" });

            var names = JobTags.GetNames(_connection.Object);

            Assert.Equal(new[] { "a", "b", "c" }, names);
            _connection.Verify(x => x.GetSetCount(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Reads_FallBackToTheBasicApi_WhenTheStorageHasNoExtendedApi()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            connection.Setup(x => x.GetAllItemsFromSet("tags")).Returns(new HashSet<string> { "a" });
            connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1", "2" });

            Assert.Equal(2, JobTags.Count(connection.Object, "a"));
            Assert.Equal(2, JobTags.GetJobIds(connection.Object, "a", 0, 10).Count);
            Assert.Equal(2, Assert.Single(JobTags.GetAll(connection.Object)).Count);
        }

        [Fact]
        public void Delete_RemovesTheMembersOneByOne_WhenTheTransactionCanNotRemoveASet()
        {
            var transaction = new Mock<JobStorageTransaction> { CallBase = true };
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            _connection.Setup(x => x.GetAllItemsFromSet("tags:a")).Returns(new HashSet<string> { "1" });

            JobTags.Delete(_connection.Object, "a");

            transaction.Verify(x => x.RemoveFromSet("tags:a", "1"), Times.Once);
            transaction.Verify(x => x.RemoveFromSet("tags", "a"), Times.Once);
            transaction.Verify(x => x.Commit(), Times.Once);
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

        [Fact, StaticLock]
        public void TagAttribute_OnCreated_LogsAWarning_InsteadOfFailingCreation_WhenIndexingFails()
        {
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import("Contoso", 3)) };
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("deadlock victim"));
            var log = new RecordingLogProvider();

            using (log.Use())
            {
                new TagAttribute("customer:{0}").OnCreated(context.GetCreatedContext("job-1"));
            }

            var entry = Assert.Single(log.Entries, x => x.Logger.EndsWith(nameof(TagAttribute), StringComparison.Ordinal));
            Assert.Equal(Masterly.NightShift.Logging.LogLevel.Warn, entry.Level);
            Assert.IsType<TimeoutException>(entry.Exception);
            Assert.Contains("job-1", entry.Message);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void Add_TagsTheJob_WhenTheApplicationsOptionsDoNotKnowStringArrays()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(string)) });

            JobTags.Add(_connection.Object, "job-1", new[] { "billing" });
            JobTags.Add(_connection.Object, "job-1", new[] { "import" });

            Assert.Equal("[\"billing\",\"import\"]", _parameters[JobTags.ParameterName]);
            Assert.Equal(new[] { "billing", "import" }, JobTags.GetTags(_connection.Object, "job-1"));
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void TagAttribute_TagsTheJob_WhenTheApplicationsOptionsDoNotKnowStringArrays()
        {
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(string)) });
            var context = new CreateContextMock { Job = Job.FromExpression(() => Import("Contoso", 3)) };
            IDictionary<string, string> stored = null;
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
            context.Connection
                .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
                .Callback<Job, IDictionary<string, string>, DateTime, TimeSpan>((_, parameters, _, _) => stored = parameters)
                .Returns("job-1");

            new TagAttribute("customer:{0}").OnCreating(context.GetCreatingContext());
            new Masterly.NightShift.Client.CoreBackgroundJobFactory(new Mock<Masterly.NightShift.States.IStateMachine>().Object).Create(context.Object);

            Assert.Equal("[\"customer:contoso\"]", stored[JobTags.ParameterName]);
            Assert.Equal(new[] { "customer:contoso" }, JobTags.Parse(stored[JobTags.ParameterName]));
        }

        [Fact, CleanSerializerSettings]
        public void Parse_ReadsTagsWrittenWithTheApplicationsOptions()
        {
            // Stored before with a converter of the application that the internal options don't have
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new CommaSeparatedConverter() } });

            Assert.Equal(new[] { "billing", "import" }, JobTags.Parse("\"billing,import\""));
        }

        private sealed class CommaSeparatedConverter : System.Text.Json.Serialization.JsonConverter<string[]>
        {
            public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return reader.GetString()!.Split(',');
            }

            public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(String.Join(",", value));
            }
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
