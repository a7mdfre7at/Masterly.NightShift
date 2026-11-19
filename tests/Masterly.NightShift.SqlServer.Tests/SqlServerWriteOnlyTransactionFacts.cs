extern alias ReferencedDapper;
using System.Data;
using System.Data.Common;
using ReferencedDapper::Dapper;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerWriteOnlyTransactionFacts
{
    private static readonly string TooLongKey = "123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_123456789_12345";
    private static readonly string TooLongTruncatedKey = TooLongKey[..100];

    private readonly PersistentJobQueueProviderCollection _queueProviders;

    public SqlServerWriteOnlyTransactionFacts()
    {
        Mock<IPersistentJobQueueProvider> defaultProvider = new();
        defaultProvider.Setup(x => x.GetJobQueue())
            .Returns(new Mock<IPersistentJobQueue>().Object);

        _queueProviders = new PersistentJobQueueProviderCollection(defaultProvider.Object);
    }

    public static IEnumerable<object[]> GetConfiguration()
    {
        yield return new object[] { /* useBatching */ false };
        yield return new object[] { /* useBatching */ true };
    }

    [Fact]
    public void Ctor_ThrowsAnException_IfConnectionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerWriteOnlyTransaction(null));

        Assert.Equal("connection", exception.ParamName);
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireJob_ThrowsAnException_WhenJobIdIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.ExpireJob(null, TimeSpan.Zero), useBatching));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireJob_SetsJobExpirationData(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            dynamic jobId = sql.Query(arrangeSql).Single().Id.ToString();
            dynamic anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

            Commit(x => x.ExpireJob(jobId, TimeSpan.FromHours(24)), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.True(DateTime.UtcNow.AddHours(23) < job.ExpireAt && job.ExpireAt < DateTime.UtcNow.AddHours(25));

            dynamic anotherJob = GetTestJob(sql, anotherJobId);
            Assert.Null(anotherJob.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireJob_KeepsTheExpirationOnTheLastDayTheColumnHolds_WhenExpireInIsTooLong(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            dynamic jobId = sql.Query(arrangeSql).Single().Id.ToString();

            Commit(x => x.ExpireJob(jobId, TimeSpan.MaxValue), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Equal(new DateTime(9999, 12, 31), job.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireCollectionsAndCounters_KeepTheExpirationOnTheLastDayTheColumnHolds_WhenExpireInIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-set", "value");
                x.InsertToList("my-list", "value");
                x.SetRangeInHash("my-hash", new Dictionary<string, string> { { "field", "value" } });
            }, useBatching);

            Commit(x =>
            {
                x.ExpireSet("my-set", TimeSpan.MaxValue);
                x.ExpireList("my-list", TimeSpan.MaxValue);
                x.ExpireHash("my-hash", TimeSpan.MaxValue);
                x.IncrementCounter("my-counter", TimeSpan.MaxValue);
                x.DecrementCounter("my-counter", TimeSpan.MaxValue);
            }, useBatching);

            DateTime[] expireAt = [.. sql.Query<DateTime>($@"
select ExpireAt from [{Constants.DefaultSchema}].[Set] union all
select ExpireAt from [{Constants.DefaultSchema}].List union all
select ExpireAt from [{Constants.DefaultSchema}].Hash union all
select ExpireAt from [{Constants.DefaultSchema}].Counter")];

            Assert.Equal(Enumerable.Repeat(new DateTime(9999, 12, 31), 5), expireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void JobWrites_DoNothing_WhenTheJobIdIsNotANumber(bool useBatching)
    {
        Mock<IState> state = new();
        state.Setup(x => x.Name).Returns("State");
        state.Setup(x => x.SerializeData()).Returns([]);

        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.ExpireJob("not-a-number", TimeSpan.FromDays(1));
                x.PersistJob("not-a-number");
                x.SetJobState("not-a-number", state.Object);
                x.AddJobState("not-a-number", state.Object);
                x.AddToQueue("default", "not-a-number");
            }, useBatching);

            Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].State"));
            Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_TakesTheExpiration_OfTheNewestItem_OfALongList(bool useBatching)
    {
        // Long enough that reading every item would escalate to a lock on the whole table
        string arrangeSql = $@"
;with n as (select top (6000) row_number() over (order by (select null)) i from sys.all_objects a cross join sys.all_objects b)
insert into [{Constants.DefaultSchema}].List ([Key], Value, ExpireAt)
select N'long-list', cast(i as nvarchar(20)), dateadd(day, 1, '2030-01-01') from n;";

        UseConnection(sql =>
        {
            sql.Execute(arrangeSql);

            Commit(x => x.InsertToList("long-list", "new"), useBatching);

            DateTime? expireAt = sql.QuerySingle<DateTime?>(
                $"select ExpireAt from [{Constants.DefaultSchema}].List where [Key] = N'long-list' and Value = N'new'");
            Assert.Equal(new DateTime(2030, 1, 2), expireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistJob_ThrowsAnException_WhenJobIdIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.PersistJob(null), useBatching));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistJob_ClearsTheJobExpirationData(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt, ExpireAt)
values ('', '', getutcdate(), getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            dynamic jobId = sql.Query(arrangeSql).Single().Id.ToString();
            dynamic anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

            Commit(x => x.PersistJob(jobId), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Null(job.ExpireAt);

            dynamic anotherJob = GetTestJob(sql, anotherJobId);
            Assert.NotNull(anotherJob.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_ThrowsAnException_InsteadOfTruncating_WhenTheStateNameIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns(new string('s', 21));

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Commit(x => x.SetJobState("1", state.Object), useBatching));

            Assert.Equal("name", exception.ParamName);
            Assert.Contains("at most 20 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].[State]"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_ThrowsAnException_InsteadOfTruncating_WhenTheStateNameIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns(new string('s', 21));

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Commit(x => x.AddJobState("1", state.Object), useBatching));

            Assert.Contains("at most 20 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_ThrowsAnException_WhenJobIdIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetJobState(null, new Mock<IState>().Object), useBatching));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_ThrowsAnException_WhenStateIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetJobState("my-job", null), useBatching));

            Assert.Equal("state", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_AppendsAStateAndSetItToTheJob(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            string jobId = sql.Query(arrangeSql).Single().Id.ToString();
            dynamic anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns("Reason");
            state.Setup(x => x.SerializeData())
                .Returns(new Dictionary<string, string> { { "Name", "Value" } });

            Commit(x => x.SetJobState(jobId, state.Object), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Equal("State", job.StateName);
            Assert.NotNull(job.StateId);

            dynamic anotherJob = GetTestJob(sql, anotherJobId);
            Assert.Null(anotherJob.StateName);
            Assert.Null(anotherJob.StateId);

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();
            Assert.Equal(jobId, jobState.JobId.ToString());
            Assert.Equal("State", jobState.Name);
            Assert.Equal("Reason", jobState.Reason);
            Assert.NotNull(jobState.CreatedAt);
            Assert.Equal("{\"Name\":\"Value\"}", jobState.Data);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_CanBeCalledWithNullReasonAndData(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            dynamic jobId = sql.Query(arrangeSql).Single().Id.ToString();

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns(default(string));
            state.Setup(x => x.SerializeData()).Returns(default(Dictionary<string, string>));

            Commit(x => x.SetJobState(jobId, state.Object), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Equal("State", job.StateName);
            Assert.NotNull(job.StateId);

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();
            Assert.Equal("State", jobState.Name);
            Assert.Null((object)jobState.Reason);
            Assert.Null((object)jobState.Data);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_ThrowsAnException_WhenJobIdIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddJobState(null, new Mock<IState>().Object), useBatching));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_ThrowsAnException_WhenStateIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddJobState("my-job", null), useBatching));

            Assert.Equal("state", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_JustAddsANewRecordInATable(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            string jobId = sql.Query(arrangeSql).Single().Id.ToString();

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns("Reason");
            state.Setup(x => x.SerializeData())
                .Returns(new Dictionary<string, string> { { "Name", "Value" } });

            Commit(x => x.AddJobState(jobId, state.Object), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Null(job.StateName);
            Assert.Null(job.StateId);

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();
            Assert.Equal(jobId, jobState.JobId.ToString());
            Assert.Equal("State", jobState.Name);
            Assert.Equal("Reason", jobState.Reason);
            Assert.NotNull(jobState.CreatedAt);
            Assert.Equal("{\"Name\":\"Value\"}", jobState.Data);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_CanBeCalledWithNullReasonAndData(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

        UseConnection(sql =>
        {
            string jobId = sql.Query(arrangeSql).Single().Id.ToString();

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");
            state.Setup(x => x.Reason).Returns(default(string));
            state.Setup(x => x.SerializeData()).Returns(default(Dictionary<string, string>));

            Commit(x => x.AddJobState(jobId, state.Object), useBatching);

            dynamic job = GetTestJob(sql, jobId);
            Assert.Null(job.StateName);
            Assert.Null(job.StateId);

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();
            Assert.Equal(jobId, jobState.JobId.ToString());
            Assert.Equal("State", jobState.Name);
            Assert.Null((object)jobState.Reason);
            Assert.NotNull(jobState.CreatedAt);
            Assert.Null((object)jobState.Data);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToQueue_ThrowsAnException_WhenQueueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToQueue(null, "my-job"), useBatching));

            Assert.Equal("queue", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToQueue_ThrowsAnException_WhenJobIdIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToQueue("my-queue", null), useBatching));

            Assert.Equal("jobId", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [InlineData(false), InlineData(true)]
    public void AddToQueue_CallsEnqueue_OnTargetPersistentQueue(bool useBatching)
    {
        Mock<IPersistentJobQueue> correctJobQueue = new();
        Mock<IPersistentJobQueueProvider> correctProvider = new();
        correctProvider.Setup(x => x.GetJobQueue())
            .Returns(correctJobQueue.Object);

        _queueProviders.Add(correctProvider.Object, ["default"]);

        UseConnection(sql =>
        {
            Commit(x => x.AddToQueue("default", "1"), useBatching);

            correctJobQueue.Verify(x => x.Enqueue(
                It.IsNotNull<System.Data.Common.DbConnection>(),
                It.IsNotNull<System.Data.Common.DbTransaction>(),
                "default",
                "1"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToQueue_EnqueuesAJobDirectly_WhenDefaultQueueProviderIsUsed(bool useBatching)
    {
        // We are relying on the fact that SqlServerJobQueue.Enqueue method will throw, because its storage
        // points to a schema that doesn't exist. If we don't see this exception, and if the record is
        // inserted, then everything is fine.
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false, SchemaName = "NonExistingSchema" };
        _queueProviders.Add(
            new SqlServerJobQueueProvider(new Mock<SqlServerStorage>("connection=false;", options).Object, options),
            ["default"]);

        UseConnection(sql =>
        {
            Commit(x => x.AddToQueue("default", "1"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].JobQueue").Single();
            Assert.Equal("1", record.JobId.ToString());
            Assert.Equal("default", record.Queue);
            Assert.Null(record.FetchedAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void Enqueue_ThrowsAnException_WhenTheGivenQueueIsTooLong(bool useBatching)
    {
        UseConnection(connection =>
        {
            string queueName = "some-really-long-queue-name-that-should-cause-an-exception-to-be-thrown-and-not-ignored";
            // We are relying on the fact that SqlServerJobQueue.Enqueue method would fail with another error,
            // because its storage points to a schema that doesn't exist. Seeing the length check of the direct
            // insert means the record would have been inserted directly.
            SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false, SchemaName = "NonExistingSchema" };
            _queueProviders.Add(
                new SqlServerJobQueueProvider(new Mock<SqlServerStorage>("connection=false;", options).Object, options),
                [queueName]);

            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            {
                Commit(x => x.AddToQueue(queueName, "1"), useBatching);
            });

            dynamic? record = connection.Query($"select * from [{Constants.DefaultSchema}].JobQueue").SingleOrDefault();
            Assert.Null(record);
            Assert.Contains("at most 50 characters", exception.Message);
        });
    }

    private static dynamic GetTestJob(IDbConnection connection, string jobId)
    {
        return connection
            .Query($"select * from [{Constants.DefaultSchema}].Job where Id = @id", new { id = jobId })
            .Single();
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.IncrementCounter(null), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.IncrementCounter(TooLongKey), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_AddsRecordToCounterTable_WithPositiveValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.IncrementCounter("my-key"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(1, record.Value);
            DateTime? expireAt = record.ExpireAt;
            Assert.Null(expireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_WithExpiry_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.IncrementCounter(null, TimeSpan.FromHours(1)), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_WithExpiry_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.IncrementCounter(TooLongKey, TimeSpan.FromHours(1)), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_WithExpiry_AddsARecord_WithExpirationTimeSet(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.IncrementCounter("my-key", TimeSpan.FromDays(1)), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(1, record.Value);
            Assert.NotNull(record.ExpireAt);

            DateTime expireAt = record.ExpireAt;

            Assert.True(DateTime.UtcNow.AddHours(23) < expireAt);
            Assert.True(expireAt < DateTime.UtcNow.AddHours(25));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void IncrementCounter_WithExistingKey_AddsAnotherRecord(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.IncrementCounter("my-key");
                x.IncrementCounter("my-key");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal(2, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.DecrementCounter(null), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.DecrementCounter(TooLongKey), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_AddsRecordToCounterTable_WithNegativeValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.DecrementCounter("my-key"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(-1, record.Value);
            DateTime? expireAt = record.ExpireAt;
            Assert.Null(expireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_WithExpiry_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.DecrementCounter(null, TimeSpan.FromHours(1)), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_WithExpiry_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.DecrementCounter(TooLongKey, TimeSpan.FromHours(1)), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_WithExpiry_AddsARecord_WithExpirationTimeSet(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.DecrementCounter("my-key", TimeSpan.FromDays(1)), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal(-1, record.Value);
            Assert.NotNull(record.ExpireAt);

            DateTime expireAt = record.ExpireAt;

            Assert.True(DateTime.UtcNow.AddHours(23) < expireAt);
            Assert.True(expireAt < DateTime.UtcNow.AddHours(25));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void DecrementCounter_WithExistingKey_AddsAnotherRecord(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.DecrementCounter("my-key");
                x.DecrementCounter("my-key");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Counter").Single();

            Assert.Equal(2, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToSet(null, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_ThrowsAnException_WhenValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToSet("my-set", null), useBatching));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.AddToSet(TooLongKey, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_ThrowsAnException_InsteadOfTruncating_WhenValueIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.AddToSet("my-set", new string('v', 257)), useBatching));

            Assert.Equal("value", exception.ParamName);
            Assert.Contains("at most 256 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].[Set]"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_StoresAValue_OfExactly256Characters(bool useBatching)
    {
        UseConnection(sql =>
        {
            string value = new('v', 256);

            Commit(x => x.AddToSet(TooLongTruncatedKey, value), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();
            Assert.Equal(TooLongTruncatedKey, record.Key);
            Assert.Equal(value, record.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_ThrowsAnException_InsteadOfTruncating_WhenValueIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            string storedValue = new('v', 256);
            Commit(x => x.AddToSet("my-set", storedValue), useBatching);

            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.RemoveFromSet("my-set", storedValue + "-and-more"), useBatching));

            Assert.Equal("value", exception.ParamName);
            Assert.Equal(storedValue, sql.Query<string>($"select [Value] from [{Constants.DefaultSchema}].[Set]").Single());
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_AddsARecord_IfThereIsNo_SuchKeyAndValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.AddToSet("my-key", "my-value"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal("my-value", record.Value);
            Assert.Equal(0.0, record.Score, 2);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_AddsARecord_WhenKeyIsExists_ButValuesAreDifferent(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "another-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(2, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_DoesNotAddARecord_WhenBothKeyAndValueAreExist(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithScore_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToSet(null, "value", 1.2D), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithScore_ThrowsAnException_WhenValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddToSet("my-set", null, 1.2D), useBatching));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithScore_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.AddToSet(TooLongKey, "value", 1.2D), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithScore_AddsARecordWithScore_WhenBothKeyAndValueAreNotExist(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.AddToSet("my-key", "my-value", 3.2), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal("my-value", record.Value);
            Assert.Equal(3.2, record.Score, 3);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithScore_UpdatesAScore_WhenBothKeyAndValueAreExist(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.AddToSet("my-key", "my-value", 3.2);
            }, useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(3.2, record.Score, 3);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                Commit(x =>
                    x.AddToSet("my-key", "my-value", 3.2),
                    useBatching,
                    options => options.UseIgnoreDupKeyOption = true);

                dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();
                Assert.Equal(3.2, record.Score, 3);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)");
                sql.Execute($@"insert into [{Constants.DefaultSchema}].[Set] ([Key], Value, Score) VALUES
(N'my-key1', N'value1', 1.2),
(N'my-key1', N'value2', 1.2),
(N'my-key2', N'value1', 1.2)");

                Commit(x =>
                    x.AddToSet("my-key1", "value1", 2.3), useBatching, options => options.UseIgnoreDupKeyOption = true);

                dynamic record1 = sql.Query($"select * from [{Constants.DefaultSchema}].[Set] where [Key] = N'my-key1' and Value = N'value1'").Single();
                Assert.Equal(2.3, record1.Score, 3);

                dynamic record2 = sql.Query($"select * from [{Constants.DefaultSchema}].[Set] where [Key] = N'my-key1' and Value = N'value2'").Single();
                Assert.Equal(1.2, record2.Score, 3);

                dynamic record3 = sql.Query($"select * from [{Constants.DefaultSchema}].[Set] where [Key] = N'my-key2' and Value = N'value1'").Single();
                Assert.Equal(1.2, record3.Score, 3);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddToSet_WithIgnoreDupKeyOption_FailsToUpdateExistingValue_WhenIgnoreDupKeyOptionWasNotSet(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = OFF)");
                sql.Execute($"insert into [{Constants.DefaultSchema}].[Set] ([Key], Value, Score) VALUES (N'key1', N'value1', 1.2)");

                DbException exception = Assert.ThrowsAny<DbException>(() =>
                    Commit(x =>
                        x.AddToSet("key1", "value1"), useBatching, options => options.UseIgnoreDupKeyOption = true));

                Assert.Contains("Violation of PRIMARY KEY", exception.Message);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveFromSet(null, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_ThrowsAnException_WhenValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveFromSet("my-set", null), useBatching));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.AddToSet(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.RemoveFromSet(TooLongKey, "value"), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [Value] from [{Constants.DefaultSchema}].[Set] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Equal("value", result.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_RemovesARecord_WithGivenKeyAndValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("my-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(0, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_DoesNotRemoveRecord_WithSameKey_AndDifferentValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("my-key", "different-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromSet_DoesNotRemoveRecord_WithSameValue_AndDifferentKey(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.AddToSet("my-key", "my-value");
                x.RemoveFromSet("different-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.InsertToList(null, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_ThrowsAnException_WhenValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.InsertToList("my-list", null), useBatching));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.InsertToList(TooLongKey, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_AddsARecord_WithGivenValues(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.InsertToList("my-key", "my-value"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal("my-key", record.Key);
            Assert.Equal("my-value", record.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_AddsAnotherRecord_WhenBothKeyAndValueAreExist(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.InsertToList("my-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(2, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_TakesTheExpiration_OfAnExpiringList(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "1");
                x.ExpireList("my-key", TimeSpan.FromHours(1));
            }, useBatching);

            Commit(x => x.InsertToList("my-key", "2"), useBatching);

            Dictionary<string, DateTime?> records = sql.Query($"select [Value], [ExpireAt] from [{Constants.DefaultSchema}].List where [Key] = N'my-key'")
                .ToDictionary(x => (string)x.Value, x => (DateTime?)x.ExpireAt);

            Assert.NotNull(records["1"]);
            Assert.Equal(records["1"], records["2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_LeavesTheExpirationEmpty_ForAPersistentOrNewList(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("expiring", "1");
                x.ExpireList("expiring", TimeSpan.FromHours(1));
                x.InsertToList("persistent", "1");
            }, useBatching);

            Commit(x =>
            {
                x.InsertToList("persistent", "2");
                x.InsertToList("new", "1");
            }, useBatching);

            int expiring = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List where [ExpireAt] is not null").Single();
            List<string> keys = [.. sql.Query<string>($"select [Key] from [{Constants.DefaultSchema}].List where [ExpireAt] is null")];

            Assert.Equal(1, expiring);
            Assert.Equal(["new", "persistent", "persistent"], keys.OrderBy(x => x, StringComparer.Ordinal));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_AcceptsAKey_OfExactly100Characters(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.InsertToList(TooLongTruncatedKey, "value"), useBatching);

            string key = sql.Query<string>($"select [Key] from [{Constants.DefaultSchema}].List").Single();
            Assert.Equal(TooLongTruncatedKey, key);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveFromList(null, "value"), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_ThrowsAnException_WhenValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveFromList("my-list", null), useBatching));

            Assert.Equal("value", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.InsertToList(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.RemoveFromList(TooLongKey, "value"), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [Value] from [{Constants.DefaultSchema}].[List] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Equal("value", result.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_RemovesAllRecords_WithGivenKeyAndValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("my-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(0, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_DoesNotRemoveRecords_WithSameKey_ButDifferentValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("my-key", "different-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromList_DoesNotRemoveRecords_WithSameValue_ButDifferentKey(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "my-value");
                x.RemoveFromList("different-key", "my-value");
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.TrimList(null, 0, 1), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.InsertToList(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.TrimList(TooLongKey, 1, 2), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [Value] from [{Constants.DefaultSchema}].[List] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Equal("value", result.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_TrimsAList_ToASpecifiedRange(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.InsertToList("my-key", "1");
                x.InsertToList("my-key", "2");
                x.InsertToList("my-key", "3");
                x.TrimList("my-key", 1, 2);
            }, useBatching);

            dynamic[] records = [.. sql.Query($"select * from [{Constants.DefaultSchema}].List")];

            Assert.Equal(2, records.Length);
            Assert.Equal("1", records[0].Value);
            Assert.Equal("2", records[1].Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_RemovesRecordsToEnd_IfKeepAndingAt_GreaterThanMaxElementIndex(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.InsertToList("my-key", "1");
                x.InsertToList("my-key", "2");
                x.TrimList("my-key", 1, 100);
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(2, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_RemovesAllRecords_WhenStartingFromValue_GreaterThanMaxElementIndex(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("my-key", 1, 100);
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(0, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_RemovesAllRecords_IfStartFromGreaterThanEndingAt(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("my-key", 1, 0);
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(0, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void TrimList_RemovesRecords_OnlyOfAGivenKey(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x =>
            {
                x.InsertToList("my-key", "0");
                x.TrimList("another-key", 1, 0);
            }, useBatching);

            int recordCount = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single();

            Assert.Equal(1, recordCount);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetRangeInHash(null, []), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.SetRangeInHash("some-hash", null), useBatching));

            Assert.Equal("keyValuePairs", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.SetRangeInHash(
                    TooLongKey,
                    new Dictionary<string, string> { { "field", "value" } }), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_ThrowsAnException_InsteadOfTruncating_WhenAFieldIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.SetRangeInHash(
                    "some-hash",
                    new Dictionary<string, string> { { "short", "value" }, { new string('f', 101), "value" } }), useBatching));

            Assert.Equal("field", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].Hash"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_StoresAField_OfExactly100Characters(bool useBatching)
    {
        UseConnection(sql =>
        {
            string field = new('f', 100);

            Commit(x => x.SetRangeInHash(TooLongTruncatedKey, new Dictionary<string, string> { { field, "value" } }), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].Hash").Single();
            Assert.Equal(TooLongTruncatedKey, record.Key);
            Assert.Equal(field, record.Field);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_MergesAllRecords(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key1", "Value1" },
                { "Key2", "Value2" }
            }), useBatching);

            Dictionary<string, string> result = sql.Query(
                $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                new { key = "some-hash" })
                .ToDictionary(x => (string)x.Field, x => (string)x.Value);

            Assert.Equal("Value1", result["Key1"]);
            Assert.Equal("Value2", result["Key2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_CanSetANullValue(bool useBatching)
    {
        UseConnection(sql =>
        {
            Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key1", null }
            }), useBatching);

            Dictionary<string, string> result = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                    new { key = "some-hash" })
                .ToDictionary(x => (string)x.Field, x => (string)x.Value);

            Assert.Null(result["Key1"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "key", "value" }
                }), useBatching, options => options.UseIgnoreDupKeyOption = true);

                Dictionary<string, string> result = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value", result["key"]);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)");
                sql.Execute($@"insert into [{Constants.DefaultSchema}].Hash([Key], Field, Value) VALUES
(N'some-hash', N'key1', N'value1'),
(N'some-hash', N'key2', N'value1'),
(N'othr-hash', N'key1', N'value1')");

                Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "key1", "value2" }
                }), useBatching, options => options.UseIgnoreDupKeyOption = true);

                Dictionary<string, string> someResult = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value2", someResult["key1"]);
                Assert.Equal("value1", someResult["key2"]);

                Dictionary<string, string> othrResult = sql
                    .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'othr-hash'")
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("value1", othrResult["key1"]);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetRangeInHash_WithIgnoreDupKeyOption_FailsToUpdateExistingValue_WhenIgnoreDupKeyOptionWasNotSet(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = OFF)");
                sql.Execute($"insert into [{Constants.DefaultSchema}].Hash([Key], Field, Value) VALUES (N'some-hash', N'key', N'value1')");

                DbException exception = Assert.ThrowsAny<DbException>(() =>
                    Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
                    {
                        { "key", "value2" }
                    }), useBatching, options => options.UseIgnoreDupKeyOption = true));

                Assert.Contains("Violation of PRIMARY KEY", exception.Message);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveHash_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveHash(null), useBatching));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveHash_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.SetRangeInHash(
                TooLongTruncatedKey,
                new Dictionary<string, string> { { "field", "value" } }), useBatching);

            // Act
            Commit(x => x.RemoveHash(TooLongKey), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [Value] from [{Constants.DefaultSchema}].[Hash] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Equal("value", result.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveHash_RemovesAllHashRecords(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.SetRangeInHash("some-hash", new Dictionary<string, string>
            {
                { "Key1", "Value1" },
                { "Key2", "Value2" }
            }), useBatching);

            // Act
            Commit(x => x.RemoveHash("some-hash"), useBatching);

            // Assert
            int count = sql.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Hash").Single();
            Assert.Equal(0, count);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddRangeToSet(null, []), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_ThrowsAnException_WhenKeyIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                Commit(x => x.AddRangeToSet(
                    TooLongKey,
                    ["field"]), useBatching));

            Assert.Equal("key", exception.ParamName);
            Assert.Contains("at most 100 characters", exception.Message);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_ThrowsAnException_WhenItemsValueIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.AddRangeToSet("my-set", null), useBatching));

            Assert.Equal("items", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_ThrowsAnException_InsteadOfTruncating_WhenAnItemIsTooLong(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => Commit(x => x.AddRangeToSet("my-set", ["1", new('v', 257)]), useBatching));

            Assert.Equal("items", exception.ParamName);
            Assert.Contains("at most 256 characters", exception.Message);
            Assert.Empty(sql.Query($"select * from [{Constants.DefaultSchema}].[Set]"));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_AddsAllItems_ToAGivenSet(bool useBatching)
    {
        UseConnection(sql =>
        {
            List<string> items = ["1", "2", "3"];

            Commit(x => x.AddRangeToSet("my-set", items), useBatching);

            IEnumerable<string> records = sql.Query<string>($"select [Value] from [{Constants.DefaultSchema}].[Set] where [Key] = N'my-set'");
            Assert.Equal(items, records);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddRangeToSet_DoesNotFailWithException_WhenIgnoreDupKeyOptionIsSet(bool useBatching)
    {
        try
        {
            UseConnection(sql =>
            {
                sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)");
                sql.Execute($@"insert into [{Constants.DefaultSchema}].[Set] ([Key], Value, Score) VALUES
(N'my-set', N'2', 1.2),
(N'my-set', N'3', 1.2),
(N'my-set', N'4', 1.2)");

                List<string> items = ["1", "2", "3"];

                Commit(x => x.AddRangeToSet("my-set", items), useBatching);

                IEnumerable<string> records = sql.Query<string>($"select [Value] from [{Constants.DefaultSchema}].[Set] where [Key] = N'my-set'");
                Assert.Equal(["1", "2", "3", "4"], records);
            });
        }
        finally
        {
            UseConnection(sql => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Set] REBUILD WITH (IGNORE_DUP_KEY = ON)"));
        }
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.RemoveSet(null), useBatching));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveSet_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.AddToSet(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.RemoveSet(TooLongKey), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [Value] from [{Constants.DefaultSchema}].[Set] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Equal("value", result.Value);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveSet_RemovesASet_WithAGivenKey(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score]) values (@key, @value, 0.0)";

        UseConnection(sql =>
        {
            sql.Execute(arrangeSql, new[]
            {
                new { key = "set-1", value = "1" },
                new { key = "set-2", value = "1" }
            });

            Commit(x => x.RemoveSet("set-1"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").Single();
            Assert.Equal("set-2", record.Key);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireHash_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.ExpireHash(null, TimeSpan.FromMinutes(5)), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireHash_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.SetRangeInHash(
                TooLongTruncatedKey,
                new Dictionary<string, string> { { "field", "value" } }), useBatching);

            // Act
            Commit(x => x.ExpireHash(TooLongKey, TimeSpan.FromHours(1)), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[Hash] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Null(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireHash_SetsExpirationTimeOnAHash_WithGivenKey(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field])
values (@key, @field)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "hash-1", field = "field" },
                new { key = "hash-2", field = "field" }
            });

            // Act
            Commit(x => x.ExpireHash("hash-1", TimeSpan.FromMinutes(60)), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].Hash").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.True(DateTime.UtcNow.AddMinutes(59) < records["hash-1"]);
            Assert.True(records["hash-1"] < DateTime.UtcNow.AddMinutes(61));
            Assert.Null(records["hash-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.ExpireSet(null, TimeSpan.FromSeconds(45)), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireSet_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.AddToSet(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.ExpireSet(TooLongKey, TimeSpan.FromHours(1)), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[Set] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Null(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireSet_SetsExpirationTime_OnASet_WithGivenKey(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@key, @value, 0.0)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "set-1", value = "1" },
                new { key = "set-2", value = "1" }
            });

            // Act
            Commit(x => x.ExpireSet("set-1", TimeSpan.FromMinutes(60)), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.True(DateTime.UtcNow.AddMinutes(59) < records["set-1"]);
            Assert.True(records["set-1"] < DateTime.UtcNow.AddMinutes(61));
            Assert.Null(records["set-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireList_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.ExpireList(null, TimeSpan.FromSeconds(45)), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireList_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x => x.InsertToList(TooLongTruncatedKey, "value"), useBatching);

            // Act
            Commit(x => x.ExpireList(TooLongKey, TimeSpan.FromHours(1)), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[List] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.Null(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireList_SetsExpirationTime_OnAList_WithGivenKey(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[List] ([Key]) values (@key)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1", value = "1" },
                new { key = "list-2", value = "1" }
            });

            // Act
            Commit(x => x.ExpireList("list-1", TimeSpan.FromMinutes(60)), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].[List]").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.True(DateTime.UtcNow.AddMinutes(59) < records["list-1"]);
            Assert.True(records["list-1"] < DateTime.UtcNow.AddMinutes(61));
            Assert.Null(records["list-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistHash_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.PersistHash(null), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistHash_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x =>
            {
                x.SetRangeInHash(TooLongTruncatedKey, new Dictionary<string, string> { { "field", "value" } });
                x.ExpireHash(TooLongTruncatedKey, TimeSpan.FromHours(1));
            }, useBatching);

            // Act
            Commit(x => x.PersistHash(TooLongKey), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[Hash] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.NotNull(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistHash_ClearsExpirationTime_OnAGivenHash(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [ExpireAt])
values (@key, @field, @expireAt)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "hash-1", field = "field", expireAt = DateTime.UtcNow.AddDays(1) },
                new { key = "hash-2", field = "field", expireAt = DateTime.UtcNow.AddDays(1) }
            });

            // Act
            Commit(x => x.PersistHash("hash-1"), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].Hash").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.Null(records["hash-1"]);
            Assert.NotNull(records["hash-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistSet_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.PersistSet(null), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistSet_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x =>
            {
                x.AddToSet(TooLongTruncatedKey, "value");
                x.ExpireSet(TooLongTruncatedKey, TimeSpan.FromHours(1));
            }, useBatching);

            // Act
            Commit(x => x.PersistSet(TooLongKey), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[Set] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.NotNull(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistSet_ClearsExpirationTime_OnAGivenHash(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [ExpireAt], [Score])
values (@key, @value, @expireAt, 0.0)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "set-1", value = "1", expireAt = DateTime.UtcNow.AddDays(1) },
                new { key = "set-2", value = "1", expireAt = DateTime.UtcNow.AddDays(1) }
            });

            // Act
            Commit(x => x.PersistSet("set-1"), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].[Set]").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.Null(records["set-1"]);
            Assert.NotNull(records["set-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistList_ThrowsAnException_WhenKeyIsNull(bool useBatching)
    {
        UseConnection(sql =>
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => Commit(x => x.PersistList(null), useBatching));

            Assert.Equal("key", exception.ParamName);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistList_DoesNotTruncateKey_BeforeUsingIt(bool useBatching)
    {
        UseConnection(sql =>
        {
            // Arrange
            Commit(x =>
            {
                x.InsertToList(TooLongTruncatedKey, "value");
                x.ExpireList(TooLongTruncatedKey, TimeSpan.FromHours(1));
            }, useBatching);

            // Act
            Commit(x => x.PersistList(TooLongKey), useBatching);

            // Assert
            dynamic result = sql.Query(
                $"select [ExpireAt] from [{Constants.DefaultSchema}].[List] where [Key] = @key",
                new { key = TooLongTruncatedKey }).Single();

            Assert.NotNull(result.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistList_ClearsExpirationTime_OnAGivenHash(bool useBatching)
    {
        string arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[List] ([Key], [ExpireAt])
values (@key, @expireAt)";

        UseConnection(sql =>
        {
            // Arrange
            sql.Execute(arrangeSql, new[]
            {
                new { key = "list-1", expireAt = DateTime.UtcNow.AddDays(1) },
                new { key = "list-2", expireAt = DateTime.UtcNow.AddDays(1) }
            });

            // Act
            Commit(x => x.PersistList("list-1"), useBatching);

            // Assert
            Dictionary<string, DateTime?> records = sql.Query($"select * from [{Constants.DefaultSchema}].[List]").ToDictionary(x => (string)x.Key, x => (DateTime?)x.ExpireAt);
            Assert.Null(records["list-1"]);
            Assert.NotNull(records["list-2"]);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void InsertToList_HandlesListIdCanExceedInt32Max(bool useBatching)
    {
        UseConnection(sql =>
        {
            sql.Query($"DBCC CHECKIDENT('[{Constants.DefaultSchema}].List', RESEED, {int.MaxValue + 1L});");

            Commit(x => x.InsertToList("my-key", "my-value"), useBatching);

            dynamic record = sql.Query($"select * from [{Constants.DefaultSchema}].List").Single();

            Assert.True(int.MaxValue < record.Id);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void ExpireJob_SetsJobExpirationData_WhenJobIdIsLongValue(bool useBatching)
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())";

        UseConnection(sql =>
        {
            sql.Query(
                arrangeSql,
                new { jobId = int.MaxValue + 1L });

            Commit(x => x.ExpireJob((int.MaxValue + 1L).ToString(), TimeSpan.FromDays(1)), useBatching);

            dynamic job = GetTestJob(sql, (int.MaxValue + 1L).ToString());
            Assert.True(DateTime.UtcNow.AddMinutes(-1) < job.ExpireAt && job.ExpireAt <= DateTime.UtcNow.AddDays(2));
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void PersistJob_ClearsTheJobExpirationData_WhenJobIdIsLongValue(bool useBatching)
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt, ExpireAt)
values (@jobId, '', '', getutcdate(), getutcdate())";

        UseConnection(sql =>
        {
            sql.Query(
                arrangeSql,
                new { jobId = int.MaxValue + 1L });

            Commit(x => x.PersistJob((int.MaxValue + 1L).ToString()), useBatching);

            dynamic job = GetTestJob(sql, (int.MaxValue + 1L).ToString());
            Assert.Null(job.ExpireAt);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void SetJobState_WorksCorrect_WhenJobIdIsLongValue(bool useBatching)
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())";

        UseConnection(sql =>
        {
            sql.Query(
                arrangeSql,
                new { jobId = int.MaxValue + 1L });

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");

            Commit(x => x.SetJobState((int.MaxValue + 1L).ToString(), state.Object), useBatching);
            dynamic job = GetTestJob(sql, (int.MaxValue + 1L).ToString());

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();
            Assert.Equal(int.MaxValue + 1L, jobState.JobId);
            Assert.Equal(job.StateId, jobState.Id);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AddJobState_AddsAState_WhenJobIdIsLongValue(bool useBatching)
    {
        string arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())";

        UseConnection(sql =>
        {
            sql.Query(
               arrangeSql,
               new { jobId = int.MaxValue + 1L });

            Mock<IState> state = new();
            state.Setup(x => x.Name).Returns("State");

            Commit(x => x.AddJobState((int.MaxValue + 1L).ToString(), state.Object), useBatching);

            dynamic jobState = sql.Query($"select * from [{Constants.DefaultSchema}].State").Single();

            Assert.Equal(int.MaxValue + 1L, jobState.JobId);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AcquireDistributedLock_ThrowsAnException_WhenResourceIsNullOrEmpty(bool useBatching)
    {
        UseSqlServerTransaction(transaction =>
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
            () => transaction.AcquireDistributedLock("", TimeSpan.FromSeconds(15)));

            Assert.Equal("resource", exception.ParamName);
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AcquireDistributedLock_AcquiresExclusiveApplicationLock_OnSession(bool useBatching)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        SqlServerStorageOptions options = CreateOptions(useBatching);
        SqlServerStorage storage = new(sql, options);

        using SqlServerConnection connection = new(storage);
        using SqlServerWriteOnlyTransaction transaction = new(connection);
        transaction.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15));
        string lockMode = sql.Query<string>(
            $"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')").Single();

        Assert.Equal("Exclusive", lockMode);
        transaction.Commit();
    }

    [Fact, CleanDatabase]
    public void AcquireDistributedLock_ThrowsAnException_IfLockCanNotBeGranted()
    {
        using ManualResetEventSlim releaseLock = new(false);
        using ManualResetEventSlim lockAcquired = new(false);
        Exception? holderFailure = null;

        Thread thread = new(
            () => UseSqlServerTransaction(transaction1 =>
            {
                try
                {
                    transaction1.AcquireDistributedLock("exclusive", TimeSpan.Zero);

                    lockAcquired.Set();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException("Waiting for too long in transaction1 block");
                    }

                    transaction1.Commit();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Reported by the test's thread after the join: an assertion thrown here would end this thread only
                    holderFailure = ex;
                }
            }, useBatching: true));
        thread.Start();

        if (!lockAcquired.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
        {
            throw new TimeoutException("Waiting for too long in transaction2 block");
        }

        UseSqlServerTransaction(transaction2 =>
        {
            Assert.Throws<DistributedLockTimeoutException>(
                () => transaction2.AcquireDistributedLock("exclusive", TimeSpan.FromSeconds(1)));
        }, useBatching: true);

        releaseLock.Set();
        thread.Join();
        Assert.Null(holderFailure);
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]

    public void AcquireDistributedLock_TransactionCommit_ReleasesExclusiveApplicationLock(bool useBatching)
    {
        UseConnection(sql =>
        {
            SqlServerStorageOptions options = CreateOptions(useBatching);
            SqlServerStorage storage = new(sql, options);

            using (SqlServerConnection connection = new(storage))
            using (SqlServerWriteOnlyTransaction transaction = new(connection))
            {
                transaction.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15));
                transaction.Commit();
            }

            string lockMode = sql.Query<string>($"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')").Single();

            Assert.Equal("NoLock", lockMode);
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AcquireDistributedLock_TransactionCommit_DoesNotReleaseLock_IfItsOwnedByConnection(bool useBatching)
    {
        UseConnection(sql =>
        {
            SqlServerStorageOptions options = CreateOptions(useBatching);
            SqlServerStorage storage = new(sql, options);

            using (SqlServerConnection connection = new(storage))
            using (connection.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15)))
            {
                using (SqlServerWriteOnlyTransaction transaction = new(connection))
                {
                    transaction.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15));
                    transaction.Commit();
                }

                string lockMode = sql
                    .Query<string>($"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')")
                    .Single();

                Assert.Equal("Exclusive", lockMode);
            }
        });
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void AcquireDistributedLock_IsReentrant_FromTheSameTransaction_OnTheSameResource(bool useBatching)
    {
        UseSqlServerTransaction(transaction =>
        {
            transaction.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15));
            transaction.AcquireDistributedLock("hello", TimeSpan.FromSeconds(15));
            transaction.Commit();
        }, useBatching);
    }

    [Theory, CleanDatabase]
    [MemberData(nameof(GetConfiguration))]
    public void RemoveFromQueue_DeletesTheRow_WhenAKeepAliveIsDueWhileTheCommitRuns(bool useBatching)
    {
        SqlServerStorageOptions options = CreateOptions(useBatching);
        options.SlidingInvisibilityTimeout = TimeSpan.FromSeconds(5);
        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString(), options);
        UseConnection(sql => sql.Execute(
            $"insert into [{Constants.DefaultSchema}].JobQueue (JobId, Queue) values (1, N'default')"));

        using IStorageConnection connection = storage.GetConnection();
        using SqlServerTimeoutJob fetchedJob = (SqlServerTimeoutJob)connection.FetchNextJob(["default"], CancellationToken.None);
        fetchedJob.DisposeTimer();

        // Another session holds the row the transaction's set command writes, so the commit waits after its
        // commands were built, with the FetchedAt their delete matches
        using DbConnection blocker = ConnectionUtils.CreateConnection();
        using DbTransaction blocking = blocker.BeginTransaction();
        blocker.Execute(
            $"insert into [{Constants.DefaultSchema}].[Set] ([Key], Value, Score) values (N'bk:commit', N'x', 0)",
            transaction: blocking);

        using JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction();
        transaction.AddToSet("bk:commit", "x");
        transaction.RemoveFromQueue(fetchedJob);
        Thread committing = new(transaction.Commit);
        committing.Start();
        LockWaits.WaitForSessionsThatWait(1);

        // The keep-alive interval is a fifth of the invisibility timeout
        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        fetchedJob.ExecuteKeepAliveQueryIfRequired();

        blocking.Rollback();
        committing.Join();
        UseConnection(sql => Assert.Equal(
            0,
            sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobQueue")));
    }

    private static void UseConnection(Action<DbConnection> action)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        action(connection);
    }

    private void UseSqlServerConnection(
        Action<SqlServerConnection> action,
        bool useBatching,
        Action<SqlServerStorageOptions> optionsAction = null)
    {
        SqlServerStorageOptions options = CreateOptions(useBatching);
        optionsAction?.Invoke(options);

        Mock<SqlServerStorage> storage = new((Func<DbConnection>)(() => ConnectionUtils.CreateConnection()), options);
        storage.Setup(x => x.QueueProviders).Returns(_queueProviders);

        using SqlServerConnection connection = new(storage.Object);
        action(connection);
    }

    private void UseSqlServerTransaction(
        Action<SqlServerWriteOnlyTransaction> action,
        bool useBatching,
        Action<SqlServerStorageOptions> optionsAction = null)
    {
        UseSqlServerConnection(connection =>
        {
            using SqlServerWriteOnlyTransaction transaction = new(connection);
            action(transaction);
        }, useBatching, optionsAction);
    }

    private void Commit(
        Action<SqlServerWriteOnlyTransaction> action,
        bool useBatching,
        Action<SqlServerStorageOptions> optionsAction = null)
    {
        UseSqlServerTransaction(transaction =>
        {
            action(transaction);
            transaction.Commit();
        }, useBatching, optionsAction);
    }

    private static SqlServerStorageOptions CreateOptions(bool useBatching)
    {
        return new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = useBatching ? (TimeSpan?)TimeSpan.FromMinutes(5) : null
        };
    }
}
