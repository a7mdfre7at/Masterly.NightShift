extern alias ReferencedDapper;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Masterly.NightShift.Server;
using Masterly.NightShift.SqlServer.Entities;
using Masterly.NightShift.Storage.Monitoring;
using ReferencedDapper::Dapper;

// ReSharper disable UnusedVariable
// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerMonitoringApiFacts
{
    private readonly DateTime _utcNow = DateTime.UtcNow;

    [Fact, CleanSerializerSettings]
    public void HandlesChangingProcessOfServerDataSerialization()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);

        ServerData serverData = new()
        {
            WorkerCount = 5,
            Queues = ["default", "critical"],
            StartedAt = new DateTime(2016, 12, 01, 14, 33, 00)
        };
        string serializedServerData = SerializationHelper.Serialize(serverData, SerializationOption.User);

        ServerData? deserializedServerData = SerializationHelper.Deserialize<ServerData>(serializedServerData);

        Assert.Equal(5, deserializedServerData.WorkerCount);
        Assert.Equal(new[] { "default", "critical" }, deserializedServerData.Queues);
        Assert.Equal(new DateTime(2016, 12, 01, 14, 33, 00), deserializedServerData.StartedAt);
    }

    [Fact, CleanDatabase]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerMonitoringApi(null, null));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void Queues_ReturnsEmptyCollection_WhenThereAreNoQueues()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void Queues_ReturnsCorrectJobs_WhichWillBeDequeuedNext()
    {
        // Arrange
        string jobId = SimpleEnqueueJob(
            "critical",
            job: Job.FromExpression(() => Empty()),
            state: new EnqueuedState());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        QueueWithTopEnqueuedJobsDto criticalQueue = result.Single();
        KeyValuePair<string, EnqueuedJobDto?> queuedJob = criticalQueue.FirstJobs.Single();

        Assert.Equal("critical", criticalQueue.Name);
        Assert.Equal(1, criticalQueue.Length);
        Assert.Equal(jobId, queuedJob.Key);

        Assert.True(queuedJob.Value.InEnqueuedState);
        Assert.Equal("Enqueued", queuedJob.Value.State, StringComparer.OrdinalIgnoreCase);
        AssertWithinSecond(_utcNow, queuedJob.Value.EnqueuedAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), queuedJob.Value.Job.Type);
        Assert.Equal("Empty", queuedJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void QueueCounts_ReturnsTheQueuesWithTheirCounts_LikeQueues_ButWithoutTheirFirstJobs()
    {
        SimpleEnqueueJob("critical");
        SimpleEnqueueJob("critical");
        SimpleEnqueueJob("default");
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IList<QueueWithTopEnqueuedJobsDto> queues = monitoring.Queues();
        IList<QueueWithTopEnqueuedJobsDto> counts = monitoring.QueueCounts();

        Assert.Equal(queues.Select(q => (q.Name, q.Length, q.Fetched)), counts.Select(q => (q.Name, q.Length, q.Fetched)));
        Assert.Equal([("critical", 2L), ("default", 1L)], counts.Select(q => (q.Name, q.Length)));
        Assert.All(counts, q => Assert.Empty(q.FirstJobs));
        Assert.NotEmpty(queues[0].FirstJobs);
    }

    [Fact, CleanDatabase]
    public void QueueCounts_ReturnsEmptyCollection_WhenThereAreNoQueues()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        Assert.Empty(monitoring.QueueCounts());
    }

    [Fact, CleanDatabase]
    public void Storage_AdvertisesTheQueueCountsFeature()
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);

        Assert.True(storage.HasFeature(JobStorageFeatures.Monitoring.QueueCounts));
    }

    [Fact, CleanDatabase]
    public void Queues_IsAbleToHandle_JobIdWithoutCorrespondingBackgroundJobEntry()
    {
        // Arrange
        SimpleEnqueueJob("default", jobId: "41423", noState: true);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single().FirstJobs.Single();

        Assert.Equal("41423", someJob.Key);
        Assert.Null(someJob.Value);
    }

    [Fact, CleanDatabase]
    public void Queues_IsAbleToHandle_BackgroundJobEntry_WithNullState()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("test", noState: true);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single().FirstJobs.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.False(someJob.Value.InEnqueuedState);
        Assert.Null(someJob.Value.State);
        Assert.Null(someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void Queues_IsAbleToHandle_BackgroundJobEntry_WithAnotherState()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("default", state: new DeletedState());
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single().FirstJobs.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.False(someJob.Value.InEnqueuedState);
        Assert.Equal("Deleted", someJob.Value.State, StringComparer.OrdinalIgnoreCase);
        Assert.Null(someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void Queues_IsAbleToHandle_EnqueuedLikeStates_AsEnqueued()
    {
        // Arrange
        Mock<IState> state = new();
        state.SetupGet(x => x.Name).Returns("EnQUEued");

        string jobId = SimpleEnqueueJob("default", state: state.Object);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single().FirstJobs.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.True(someJob.Value.InEnqueuedState);
        Assert.Equal("EnQUEued", someJob.Value.State);
        AssertWithinSecond(_utcNow, someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void Queues_ReturnsTop5Jobs_FromItsHead()
    {
        // Arrange
        string jobId1 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId2 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId3 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId4 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId5 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId6 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId7 = SimpleEnqueueJob("critical", state: new EnqueuedState());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        QueueWithTopEnqueuedJobsDto defaultQueue = result.Single(x => x.Name == "default");
        Assert.Equal(6, defaultQueue.Length);
        Assert.Equal(5, defaultQueue.FirstJobs.Count);
        Assert.Equal(jobId1, defaultQueue.FirstJobs.First().Key);
        Assert.Equal(jobId5, defaultQueue.FirstJobs.Last().Key);

        QueueWithTopEnqueuedJobsDto criticalQueue = result.Single(x => x.Name == "critical");
        Assert.Equal(1, criticalQueue.Length);
        Assert.Single(criticalQueue.FirstJobs);
        Assert.Equal(jobId7, criticalQueue.FirstJobs.Single().Key);
    }

    [Fact, CleanDatabase]
    public void Queues_IsAbleToHandleSerializationProblems_InJobs()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("default", state: new EnqueuedState());

        UseSqlConnection(connection =>
        {
            InvocationData wrongData = new("asfasf", "232", "afasf", "gg");
            string? payload = wrongData.SerializePayload(excludeArguments: true);
            connection.Execute(
                $"update [{Constants.DefaultSchema}].Job set InvocationData = @data, Arguments = @args where Id = @jobId",
                new { jobId, data = payload, args = wrongData.Arguments });
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<QueueWithTopEnqueuedJobsDto> result = monitoring.Queues();

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> queuedJob = result.Single().FirstJobs.Single();
        Assert.Equal(jobId, queuedJob.Key);
        Assert.Null(queuedJob.Value.Job);
        Assert.True(queuedJob.Value.InEnqueuedState);
        Assert.Equal("Enqueued", queuedJob.Value.State, StringComparer.OrdinalIgnoreCase);
    }

    [Fact, CleanDatabase]
    public void Queues_ProducesSortedList_RegardlessOfActualEnqueueOrder()
    {
        // Arrange
        SimpleEnqueueJob("b-queue", state: new EnqueuedState());
        SimpleEnqueueJob("a-queue", state: new EnqueuedState());
        SimpleEnqueueJob("c-queue", state: new EnqueuedState());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        QueueWithTopEnqueuedJobsDto[] result = [.. monitoring.Queues()];

        // Assert
        Assert.Equal("a-queue", result[0].Name);
        Assert.Equal("b-queue", result[1].Name);
        Assert.Equal("c-queue", result[2].Name);
    }

    [Fact, CleanDatabase]
    public void Servers_ReturnsEmptyCollection_WhenThereAreNoServers()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IList<ServerDto> result = monitoring.Servers();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void Servers_ReturnsAllTheRegisteredServers_WithCorrectDetails()
    {
        // Arrange
        UseConnection(connection =>
        {
            connection.AnnounceServer("server1", new ServerContext { Queues = ["default"], WorkerCount = 100 });
            connection.AnnounceServer("server2", new ServerContext { Queues = ["critical"], WorkerCount = 17 });
            connection.AnnounceServer("server3", new ServerContext { Queues = ["alpha", "beta"], WorkerCount = 0 });
            return true;
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        IList<ServerDto> result = monitoring.Servers();

        // Assert
        Assert.Equal(3, result.Count);

        ServerDto server1 = result.Single(x => x.Name == "server1");
        Assert.Equal(["default"], server1.Queues);
        Assert.Equal(100, server1.WorkersCount);
        AssertWithinSecond(_utcNow, server1.StartedAt);
        AssertWithinSecond(_utcNow, server1.Heartbeat);

        ServerDto server2 = result.Single(x => x.Name == "server2");
        Assert.Equal(["critical"], server2.Queues);
        Assert.Equal(17, server2.WorkersCount);
        AssertWithinSecond(_utcNow, server2.StartedAt);
        AssertWithinSecond(_utcNow, server2.Heartbeat);

        ServerDto server3 = result.Single(x => x.Name == "server3");
        Assert.Equal(["alpha", "beta"], server3.Queues);
        Assert.Equal(0, server3.WorkersCount);
        AssertWithinSecond(_utcNow, server3.StartedAt);
        AssertWithinSecond(_utcNow, server3.Heartbeat);
    }

    [Fact, CleanDatabase]
    public void Servers_ProducesSortedList_RegardlessOfActualAnnouncementOrder()
    {
        // Arrange
        UseConnection(connection =>
        {
            connection.AnnounceServer("server3", new ServerContext { Queues = ["default"], WorkerCount = 4 });
            connection.AnnounceServer("server1", new ServerContext { Queues = ["default"], WorkerCount = 4 });
            connection.AnnounceServer("server2", new ServerContext { Queues = ["default"], WorkerCount = 4 });
            return true;
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        ServerDto[] result = [.. monitoring.Servers()];

        // Assert
        Assert.Equal("server1", result[0].Name);
        Assert.Equal("server2", result[1].Name);
        Assert.Equal("server3", result[2].Name);
    }

    [Fact, CleanDatabase]
    public void JobDetails_ThrowsAnException_WhenJobIdIsNull()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => monitoring.JobDetails(null));

        Assert.Equal("jobId", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void JobDetails_ReturnsNull_WhenTargetJobDoesNotExist()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobDetailsDto? result = monitoring.JobDetails("1412");

        Assert.Null(result);
    }

    [Theory, CleanDatabase]
    [InlineData("abc"), InlineData(""), InlineData("1.5"), InlineData("99999999999999999999")]
    public void JobDetails_ReturnsNull_WhenJobIdIsNotABigint(string jobId)
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobDetailsDto? result = monitoring.JobDetails(jobId);

        Assert.Null(result);
    }

    [Fact, CleanDatabase]
    public void JobDetails_CorrectlyHandles_CreatedButNotInitializedJob()
    {
        // Arrange
        string jobId = UseConnection(connection => connection.CreateExpiredJob(
                Job.FromExpression(() => Empty()),
                new Dictionary<string, string> { { "CurrentCulture", "en-US" }, { "RetryCount", "5" } },
                _utcNow,
                TimeSpan.FromMinutes(37)));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobDetailsDto? result = monitoring.JobDetails(jobId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(typeof(SqlServerMonitoringApiFacts), result.Job.Type);
        Assert.Equal("Empty", result.Job.Method.Name);
        Assert.Equal("en-US", result.Properties["CurrentCulture"]);
        Assert.Equal("5", result.Properties["RetryCount"]);
        AssertWithinSecond(_utcNow, result.CreatedAt);
        AssertWithinSecond(_utcNow.AddMinutes(37), result.ExpireAt);
    }

    [Fact, CleanDatabase]
    public void JobDetails_CorrectlyShowsCreated_AndInitializedJob()
    {
        // Arrange
        string createdId = UseConnection(connection =>
        {
            string jobId = connection.CreateExpiredJob(
                Job.FromExpression(() => Empty()),
                new Dictionary<string, string>(),
                _utcNow,
                TimeSpan.FromMinutes(1));

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.SetJobState(jobId, new EnqueuedState("critical") { Reason = "Some reason" });
                transaction.Commit();
            }

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                transaction.SetJobState(jobId, new DeletedState());
                transaction.Commit();
            }

            return jobId;
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobDetailsDto? result = monitoring.JobDetails(createdId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Deleted", result.History.First().StateName);
        Assert.Null(result.History.First().Reason);
        AssertWithinSecond(_utcNow, result.History.First().CreatedAt);

        Assert.Equal("Enqueued", result.History.Last().StateName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Some reason", result.History.Last().Reason);
        Assert.Equal("critical", result.History.Last().Data["Queue"]);
        AssertWithinSecond(_utcNow.Add(TimeSpan.FromMinutes(-1)), result.History.Last().CreatedAt);
    }

    [Fact, CleanDatabase]
    public void JobDetails_IsAbleToHandleSerializationProblems()
    {
        // Arrange
        string jobId = UseConnection(connection => connection.CreateExpiredJob(
            Job.FromExpression(() => Empty()),
            new Dictionary<string, string>(),
            _utcNow,
            TimeSpan.FromMinutes(1)));

        UseSqlConnection(connection =>
        {
            InvocationData wrongData = new("asfasf", "232", "afasf", "gg");
            string? payload = wrongData.SerializePayload(excludeArguments: true);
            connection.Execute(
                $"update [{Constants.DefaultSchema}].Job set InvocationData = @data, Arguments = @args where Id = @jobId",
                new { jobId, data = payload, args = wrongData.Arguments });
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobDetailsDto? result = monitoring.JobDetails(jobId);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.Job);
        Assert.All(result.Properties, pair => Assert.StartsWith("DBG_", pair.Key));
        Assert.Empty(result.History);
        AssertWithinSecond(_utcNow, result.CreatedAt);
        AssertWithinSecond(_utcNow, result.ExpireAt);
    }

    [Fact, CleanDatabase]
    public void GetStatistics_ReturnsEmptyStatistics_WhenNothingIsCreatedYet()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        StatisticsDto result = monitoring.GetStatistics();

        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Enqueued);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Processing);
        Assert.Equal(0, result.Queues);
        Assert.Equal(0, result.Recurring);
        Assert.Equal(0, result.Scheduled);
        Assert.Equal(0, result.Servers);
        Assert.Equal(0, result.Succeeded);
#if !NIGHTSHIFT_170 
        Assert.Equal(0, result.Retries);
#endif
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_ThrowsAnException_WhenQueueNamesIsNull()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => monitoring.EnqueuedJobs(null, 0, 10));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_ReturnsEmptyCollection_WhenThereIsNoSuchQueue()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("critical", 0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_ReturnsCorrectJobs_WhichWillBeDequeuedNext()
    {
        // Arrange
        string jobId = SimpleEnqueueJob(
            "critical",
            job: Job.FromExpression(() => Empty()),
            state: new EnqueuedState());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("critical", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> queuedJob = result.Single();

        Assert.Equal(jobId, queuedJob.Key);
        Assert.True(queuedJob.Value.InEnqueuedState);
        Assert.Equal("Enqueued", queuedJob.Value.State, StringComparer.OrdinalIgnoreCase);
        AssertWithinSecond(_utcNow, queuedJob.Value.EnqueuedAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), queuedJob.Value.Job.Type);
        Assert.Equal("Empty", queuedJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_ReturnsJobs_InTheAscendingOrder()
    {
        // Arrange
        string jobId1 = SimpleEnqueueJob("critical");
        string jobId2 = SimpleEnqueueJob("critical");
        string jobId3 = SimpleEnqueueJob("critical");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, EnqueuedJobDto?>[] result = [.. monitoring.EnqueuedJobs("critical", 0, 10)];

        // Assert
        Assert.Equal(jobId1, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId3, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandle_JobIdWithoutCorrespondingBackgroundJobEntry()
    {
        // Arrange
        SimpleEnqueueJob("default", jobId: "12345", noState: true);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("default", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single();

        Assert.Equal("12345", someJob.Key);
        Assert.Null(someJob.Value);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandle_BackgroundJobEntry_WithNullState()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("test", noState: true);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("test", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.False(someJob.Value.InEnqueuedState);
        Assert.Null(someJob.Value.State);
        Assert.Null(someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandle_MultipleEntriesWithTheSameJobId()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("default");
        string sameId = SimpleEnqueueJob("default", jobId: jobId);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, EnqueuedJobDto?>[] enqueued = [.. monitoring.EnqueuedJobs("default", 0, 10)];

        // Assert
        Assert.Equal(2, enqueued.Length);
        Assert.All(enqueued, x => Assert.Equal(jobId, x.Key));
        Assert.All(enqueued, x => Assert.Equal("Enqueued", x.Value.State));
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandle_BackgroundJobEntry_WithAnotherState()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("default", state: new DeletedState());
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("default", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.False(someJob.Value.InEnqueuedState);
        Assert.Equal("Deleted", someJob.Value.State, StringComparer.OrdinalIgnoreCase);
        Assert.Null(someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandle_EnqueuedLikeStates_AsEnqueued()
    {
        // Arrange
        Mock<IState> state = new();
        state.SetupGet(x => x.Name).Returns("EnQUEued");

        string jobId = SimpleEnqueueJob("default", state: state.Object);
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("default", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> someJob = result.Single();

        Assert.Equal(jobId, someJob.Key);
        Assert.True(someJob.Value.InEnqueuedState);
        Assert.Equal("EnQUEued", someJob.Value.State);
        AssertWithinSecond(_utcNow, someJob.Value.EnqueuedAt);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_ReturnsJobs_WithinTheGivenRange_FromTheGivenQueue()
    {
        // Arrange
        string jobId1 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId2 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobIdX = SimpleEnqueueJob("critical", state: new EnqueuedState());
        string jobId3 = SimpleEnqueueJob("default", state: new EnqueuedState());
        string jobId4 = SimpleEnqueueJob("default", state: new EnqueuedState());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("default", 1, 2);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(jobId2, result.First().Key);
        Assert.Equal(jobId3, result.Last().Key);
    }

    [Fact, CleanDatabase]
    public void EnqueuedJobs_IsAbleToHandleSerializationProblems_InJobs()
    {
        // Arrange
        string jobId = SimpleEnqueueJob("default", state: new EnqueuedState());

        UseSqlConnection(connection =>
        {
            InvocationData wrongData = new("asfasf", "232", "afasf", "gg");
            string? payload = wrongData.SerializePayload(excludeArguments: true);
            connection.Execute(
                $"update [{Constants.DefaultSchema}].Job set InvocationData = @data, Arguments = @args where Id = @jobId",
                new { jobId, data = payload, args = wrongData.Arguments });
        });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<EnqueuedJobDto?> result = monitoring.EnqueuedJobs("default", 0, 10);

        // Assert
        KeyValuePair<string, EnqueuedJobDto?> queuedJob = result.Single();
        Assert.Equal(jobId, queuedJob.Key);
        Assert.Null(queuedJob.Value.Job);
        Assert.True(queuedJob.Value.InEnqueuedState);
        Assert.Equal("Enqueued", queuedJob.Value.State, StringComparer.OrdinalIgnoreCase);
    }

    [Fact, CleanDatabase]
    public void FetchedJobs_ThrowsAnException_WhenQueueIsNull()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => monitoring.FetchedJobs(null, 0, 10));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void FetchedJobs_ReturnsEmptyCollection()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<FetchedJobDto?> result = monitoring.FetchedJobs("default", 0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void ProcessingJobs_ReturnsEmptyCollection_WhenThereAreNoProcessingJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<ProcessingJobDto?> result = monitoring.ProcessingJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void ProcessingJobs_ReturnsCorrectJobs_InTheProcessingState()
    {
        // Arrange
        string jobId = SimpleProcessingJob("server-1", "worker-1", job: Job.FromExpression(() => Empty()));
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<ProcessingJobDto?> result = monitoring.ProcessingJobs(0, 10);

        // Assert
        KeyValuePair<string, ProcessingJobDto?> processingJob = result.Single();

        Assert.Equal(jobId, processingJob.Key);
        Assert.True(processingJob.Value.InProcessingState);
        Assert.Equal("server-1", processingJob.Value.ServerId);
        AssertWithinSecond(_utcNow, processingJob.Value.StartedAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), processingJob.Value.Job.Type);
        Assert.Equal("Empty", processingJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void ProcessingJobs_ReturnsJobs_InTheAscendingOrder()
    {
        // Arrange
        string jobId1 = SimpleProcessingJob();
        string jobId2 = SimpleProcessingJob();
        string jobId3 = SimpleProcessingJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, ProcessingJobDto?>[] result = [.. monitoring.ProcessingJobs(0, 10)];

        // Assert
        Assert.Equal(jobId1, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId3, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void ProcessingJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleProcessingJob();
        string jobId2 = SimpleProcessingJob();
        string jobId3 = SimpleProcessingJob();
        string jobId4 = SimpleProcessingJob();
        string jobId5 = SimpleProcessingJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, ProcessingJobDto?>[] result = [.. monitoring.ProcessingJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId2, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void ScheduledJobs_ReturnsEmptyCollection_WhenThereAreNoScheduledJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<ScheduledJobDto?> result = monitoring.ScheduledJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void ScheduledJobs_ReturnsCorrectJobs_InTheScheduledState_WithNonCompositeIndex()
    {
        // Arrange
        string jobId = SimpleScheduledJob(
            TimeSpan.FromDays(1),
            job: Job.FromExpression(() => Empty()));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<ScheduledJobDto?> result = monitoring.ScheduledJobs(0, 10);

        // Assert
        KeyValuePair<string, ScheduledJobDto?> scheduledJob = result.Single();

        Assert.Equal(jobId, scheduledJob.Key);
        Assert.True(scheduledJob.Value.InScheduledState);
        AssertWithinSecond(_utcNow.Add(TimeSpan.FromDays(1)), scheduledJob.Value.EnqueueAt);
        AssertWithinSecond(_utcNow, scheduledJob.Value.ScheduledAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), scheduledJob.Value.Job.Type);
        Assert.Equal("Empty", scheduledJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void ScheduledJobs_ReturnsCorrectJobs_InTheScheduledState_WithCompositeIndex()
    {
        // Arrange
        string jobId = SimpleScheduledJob(
            TimeSpan.FromHours(1),
            job: Job.FromExpression(() => Empty(), "critical"));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<ScheduledJobDto?> result = monitoring.ScheduledJobs(0, 10);

        // Assert
        KeyValuePair<string, ScheduledJobDto?> scheduledJob = result.Single();
        Assert.Equal(jobId, scheduledJob.Key);
        Assert.Equal("critical", scheduledJob.Value.Job.Queue);
    }

    [Fact, CleanDatabase]
    public void ScheduledJobs_ReturnsJobs_InTheAscendingOrder()
    {
        // Arrange
        string jobId1 = SimpleScheduledJob();
        string jobId2 = SimpleScheduledJob();
        string jobId3 = SimpleScheduledJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, ScheduledJobDto?>[] result = [.. monitoring.ScheduledJobs(0, 10)];

        // Assert
        Assert.Equal(jobId1, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId3, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void ScheduledJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleScheduledJob();
        string jobId2 = SimpleScheduledJob();
        string jobId3 = SimpleScheduledJob();
        string jobId4 = SimpleScheduledJob();
        string jobId5 = SimpleScheduledJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, ScheduledJobDto?>[] result = [.. monitoring.ScheduledJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId2, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void SucceededJobs_ReturnsEmptyCollection_WhenThereAreNoSucceededJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<SucceededJobDto?> result = monitoring.SucceededJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void SucceededJobs_ReturnsCorrectJobs_InTheSucceededState()
    {
        // Arrange
        string jobId = SimpleSucceededJob(
            "hello", 123, 456,
            job: Job.FromExpression(() => Empty()));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<SucceededJobDto?> result = monitoring.SucceededJobs(0, 10);

        // Assert
        KeyValuePair<string, SucceededJobDto?> succeededJob = result.Single();

        Assert.Equal(jobId, succeededJob.Key);
        Assert.True(succeededJob.Value.InSucceededState);
        Assert.Equal("\"hello\"", succeededJob.Value.Result);
        Assert.Equal(123 + 456, succeededJob.Value.TotalDuration);
        AssertWithinSecond(_utcNow, succeededJob.Value.SucceededAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), succeededJob.Value.Job.Type);
        Assert.Equal("Empty", succeededJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void SucceededJobs_ReturnsJobs_InTheDescendingOrder()
    {
        // Arrange
        string jobId1 = SimpleSucceededJob();
        string jobId2 = SimpleSucceededJob();
        string jobId3 = SimpleSucceededJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, SucceededJobDto?>[] result = [.. monitoring.SucceededJobs(0, 10)];

        // Assert
        Assert.Equal(jobId3, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId1, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void SucceededJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleSucceededJob();
        string jobId2 = SimpleSucceededJob();
        string jobId3 = SimpleSucceededJob();
        string jobId4 = SimpleSucceededJob();
        string jobId5 = SimpleSucceededJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, SucceededJobDto?>[] result = [.. monitoring.SucceededJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId4, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void SucceededJobs_IsAbleToHandle_ExpiredJobEntry()
    {
        // Arrange
        string jobId1 = SimpleSucceededJob(expireIn: TimeSpan.FromHours(-1));
        UseSqlConnection(connection => connection.Execute(
            $"delete from[{Constants.DefaultSchema}].Job where ExpireAt is not null"));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, SucceededJobDto?> someJob = monitoring.SucceededJobs(0, 10).SingleOrDefault();

        // Assert
        Assert.True(someJob.Key == null || someJob.Key == jobId1, someJob.Key);
        Assert.Null(someJob.Value);
    }

    [Fact, CleanDatabase]
    public void FailedJobs_ReturnsEmptyCollection_WhenThereAreNoFailedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<FailedJobDto?> result = monitoring.FailedJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void FailedJobs_ReturnsCorrectJobs_InTheFailedState()
    {
        // Arrange
        string jobId = SimpleJob(
            job: Job.FromExpression(() => Empty()),
            state: new FailedState(new InvalidOperationException("Hello, world!")) { Reason = "Some reason" });

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<FailedJobDto?> result = monitoring.FailedJobs(0, 10);

        // Assert
        KeyValuePair<string, FailedJobDto?> failedJob = result.Single();

        Assert.Equal(jobId, failedJob.Key);
        Assert.True(failedJob.Value.InFailedState);
        Assert.Equal("Some reason", failedJob.Value.Reason);
        Assert.Equal(typeof(InvalidOperationException).FullName, failedJob.Value.ExceptionType);
        Assert.Equal("Hello, world!", failedJob.Value.ExceptionMessage);
        AssertWithinSecond(_utcNow, failedJob.Value.FailedAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), failedJob.Value.Job.Type);
        Assert.Equal("Empty", failedJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void FailedJobs_ReturnsJobs_InTheDescendingOrder()
    {
        // Arrange
        string jobId1 = SimpleFailedJob(new InvalidOperationException());
        string jobId2 = SimpleFailedJob(new OperationCanceledException());
        string jobId3 = SimpleFailedJob(new NotSupportedException());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, FailedJobDto?>[] result = [.. monitoring.FailedJobs(0, 10)];

        // Assert
        Assert.Equal(jobId3, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId1, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void FailedJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleFailedJob();
        string jobId2 = SimpleFailedJob();
        string jobId3 = SimpleFailedJob();
        string jobId4 = SimpleFailedJob();
        string jobId5 = SimpleFailedJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, FailedJobDto?>[] result = [.. monitoring.FailedJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId4, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void DeletedJobs_ReturnsEmptyCollection_WhenThereAreNoDeletedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<DeletedJobDto?> result = monitoring.DeletedJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void DeletedJobs_ReturnsCorrectJobs_InTheDeletedState()
    {
        // Arrange
        string jobId = SimpleDeletedJob(
            exception: new InvalidOperationException("Hello, world!"),
            job: Job.FromExpression(() => Empty()));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<DeletedJobDto?> result = monitoring.DeletedJobs(0, 10);

        // Assert
        KeyValuePair<string, DeletedJobDto?> deletedJob = result.Single();

        Assert.Equal(jobId, deletedJob.Key);
        Assert.True(deletedJob.Value.InDeletedState);
        AssertWithinSecond(_utcNow, deletedJob.Value.DeletedAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), deletedJob.Value.Job.Type);
        Assert.Equal("Empty", deletedJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void DeletedJobs_ReturnsJobs_InTheDescendingOrder()
    {
        // Arrange
        string jobId1 = SimpleDeletedJob(new InvalidOperationException());
        string jobId2 = SimpleDeletedJob(new NotSupportedException());
        string jobId3 = SimpleDeletedJob(new ArgumentNullException());

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, DeletedJobDto?>[] result = [.. monitoring.DeletedJobs(0, 10)];

        // Assert
        Assert.Equal(jobId3, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId1, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void DeletedJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleDeletedJob();
        string jobId2 = SimpleDeletedJob();
        string jobId3 = SimpleDeletedJob();
        string jobId4 = SimpleDeletedJob();
        string jobId5 = SimpleDeletedJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, DeletedJobDto?>[] result = [.. monitoring.DeletedJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId4, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void DeletedJobs_IsAbleToHandle_ExpiredJobEntry()
    {
        // Arrange
        string jobId1 = SimpleDeletedJob(expireIn: TimeSpan.FromHours(-1));
        UseSqlConnection(connection => connection.Execute(
            $"delete from[{Constants.DefaultSchema}].Job where ExpireAt is not null"));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, DeletedJobDto?> someJob = monitoring.DeletedJobs(0, 10).SingleOrDefault();

        // Assert
        Assert.True(someJob.Key == null || someJob.Key == jobId1, someJob.Key);
        Assert.Null(someJob.Value);
    }

    [Fact, CleanDatabase]
    public void AwaitingJobs_ReturnsEmptyCollection_WhenThereAreNoAwaitingJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        JobList<AwaitingJobDto?> result = monitoring.AwaitingJobs(0, 10);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact, CleanDatabase]
    public void AwaitingJobs_ReturnsCorrectJobs_InTheAwaitingState()
    {
        // Arrange
        string processingId = SimpleProcessingJob("server-1", "worker-1");
        string jobId = SimpleAwaitingJob(
            processingId,
            job: Job.FromExpression(() => Empty()));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<AwaitingJobDto?> result = monitoring.AwaitingJobs(0, 10);

        // Assert
        KeyValuePair<string, AwaitingJobDto?> awaitingJob = result.Single();

        Assert.Equal(jobId, awaitingJob.Key);
        Assert.True(awaitingJob.Value.InAwaitingState);
        Assert.Equal("Processing", awaitingJob.Value.ParentStateName);
        AssertWithinSecond(_utcNow, awaitingJob.Value.AwaitingAt);

        Assert.Equal(typeof(SqlServerMonitoringApiFacts), awaitingJob.Value.Job.Type);
        Assert.Equal("Empty", awaitingJob.Value.Job.Method.Name);
    }

    [Fact, CleanDatabase]
    public void AwaitingJobs_DoesNotFailWhenSeveralJobs_PointToTheSameParentJob()
    {
        // Arrange
        string processingId = SimpleProcessingJob("server-1", "worker-1");
        string jobId1 = SimpleAwaitingJob(processingId);
        string jobId2 = SimpleAwaitingJob(processingId);

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        JobList<AwaitingJobDto?> result = monitoring.AwaitingJobs(0, 10);

        // Assert
        KeyValuePair<string, AwaitingJobDto?>[] awaiting = [.. result];

        Assert.Equal(2, awaiting.Length);
        Assert.Contains(jobId1, awaiting.Select(x => x.Key));
        Assert.Contains(jobId2, awaiting.Select(x => x.Key));
        Assert.All(awaiting, x => Assert.Equal("Processing", x.Value.ParentStateName));
    }

    [Fact, CleanDatabase]
    public void AwaitingJobs_ReturnsJobs_InTheAscendingOrder()
    {
        // Arrange
        string jobId1 = SimpleAwaitingJob("123");
        string jobId2 = SimpleAwaitingJob("456");
        string jobId3 = SimpleAwaitingJob("789");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, AwaitingJobDto?>[] result = [.. monitoring.AwaitingJobs(0, 10)];

        // Assert
        Assert.Equal(jobId1, result[0].Key);
        Assert.Equal(jobId2, result[1].Key);
        Assert.Equal(jobId3, result[2].Key);
    }

    [Fact, CleanDatabase]
    public void AwaitingJobs_ReturnsJobs_WithinTheGivenRange()
    {
        // Arrange
        string jobId1 = SimpleAwaitingJob("1");
        string jobId2 = SimpleAwaitingJob("1");
        string jobId3 = SimpleAwaitingJob("2");
        string jobId4 = SimpleAwaitingJob("1");
        string jobId5 = SimpleAwaitingJob("1");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        KeyValuePair<string, AwaitingJobDto?>[] result = [.. monitoring.AwaitingJobs(1, 2)];

        // Assert
        Assert.Equal(2, result.Length);
        Assert.Equal(jobId2, result[0].Key);
        Assert.Equal(jobId3, result[1].Key);
    }

    [Fact, CleanDatabase]
    public void ScheduledCount_ReturnsZero_WhenThereAreNoScheduledJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.ScheduledCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void ScheduledCount_ReturnsTheCorrectNumber_OfScheduledJobs()
    {
        // Arrange
        SimpleScheduledJob(TimeSpan.FromHours(1));
        SimpleScheduledJob(TimeSpan.FromHours(2));

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.ScheduledCount();

        // Assert
        Assert.Equal(2, result);
    }

    [Fact, CleanDatabase]
    public void EnqueuedCount_ThrowsAnException_WhenQueueNameIsNull()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => monitoring.EnqueuedCount(null));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void EnqueuedCount_ReturnsZero_WhenTargetQueueDoesNotExist()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.EnqueuedCount("critical");

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void EnqueuedCount_ReturnsTheCorrectNumber_OfEnqueuedJobs_OfTheGivenQueue()
    {
        // Arrange
        SimpleEnqueueJob("critical");
        SimpleEnqueueJob("default");
        SimpleEnqueueJob("critical");
        SimpleEnqueueJob("critical");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.EnqueuedCount("critical");

        // Assert
        Assert.Equal(3, result);
    }

    [Fact, CleanDatabase]
    public void FetchedCount_ThrowsAnException_WhenQueueIsNull()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => monitoring.FetchedCount(null));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact, CleanDatabase]
    public void FetchedCount_ReturnsZero_WhenTargetQueueDoesNotExist()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.FetchedCount("critical");

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void FailedCount_ReturnsZero_WhenThereAreNoFailedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.FailedCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void FailedCount_ReturnsTheCorrectNumber_OfFailedJobs()
    {
        // Arrange
        SimpleFailedJob();
        SimpleFailedJob();
        SimpleFailedJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.FailedCount();

        // Assert
        Assert.Equal(3, result);
    }

    [Fact, CleanDatabase]
    public void ProcessingCount_ReturnsZero_WhenThereAreNoProcessingJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.ProcessingCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void ProcessingCount_ReturnsTheCorrectNumber_OfProcessingJobs()
    {
        // Arrange
        SimpleProcessingJob("1", "1");
        SimpleProcessingJob("2", "2");
        SimpleProcessingJob("3", "3");
        SimpleProcessingJob("1", "1");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.ProcessingCount();

        // Assert
        Assert.Equal(4, result);
    }

    [Fact, CleanDatabase]
    public void SucceededListCount_ReturnsZero_WhenThereAreNoSucceededJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.SucceededListCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void SucceededListCount_ReturnsTheCorrectNumber_OfSucceededJobs_CurrentlyInIndex()
    {
        // Arrange
        SimpleSucceededJob();
        SimpleSucceededJob();
        SimpleSucceededJob();
        SimpleSucceededJob();
        SimpleSucceededJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.SucceededListCount();

        // Assert
        Assert.Equal(5, result);
    }

    [Fact, CleanDatabase]
    public void DeletedListCount_ReturnsZero_WhenThereAreNoDeletedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.DeletedListCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void DeletedListCount_ReturnsTheCorrectNumber_OfDeletedJobs_CurrentlyInIndex()
    {
        // Arrange
        SimpleDeletedJob();
        SimpleDeletedJob();
        SimpleDeletedJob();

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.DeletedListCount();

        // Assert
        Assert.Equal(3, result);
    }

    [Fact, CleanDatabase]
    public void AwaitingCount_ReturnsZero_WhenThereAreNoAwaitingJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        long result = monitoring.AwaitingCount();

        Assert.Equal(0, result);
    }

    [Fact, CleanDatabase]
    public void AwaitingCount_ReturnsTheCorrectNumber_OfAwaitingJobs()
    {
        // Arrange
        SimpleAwaitingJob("1");
        SimpleAwaitingJob("2");
        SimpleAwaitingJob("3");
        SimpleAwaitingJob("1");

        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        // Act
        long result = monitoring.AwaitingCount();

        // Assert
        Assert.Equal(4, result);
    }

    [Fact, CleanDatabase]
    public void SucceededByDatesCount_ReturnsEntriesForTheWholeWeek_EvenWhenThereAreNoSucceededJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.SucceededByDatesCount();
        DateTime date = _utcNow.Date;

        Assert.Equal(7, result.Count);
        Assert.Equal(0, result[date]);
        Assert.Equal(0, result[date.AddDays(-1)]);
        Assert.Equal(0, result[date.AddDays(-2)]);
        Assert.Equal(0, result[date.AddDays(-3)]);
        Assert.Equal(0, result[date.AddDays(-4)]);
        Assert.Equal(0, result[date.AddDays(-5)]);
        Assert.Equal(0, result[date.AddDays(-6)]);
    }

    [Fact, CleanDatabase]
    public void FailedByDatesCount_ReturnsEntriesForTheWholeWeek_EvenWhenThereAreNoFailedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.FailedByDatesCount();
        DateTime date = _utcNow.Date;

        Assert.Equal(7, result.Count);
        Assert.Equal(0, result[date]);
        Assert.Equal(0, result[date.AddDays(-1)]);
        Assert.Equal(0, result[date.AddDays(-2)]);
        Assert.Equal(0, result[date.AddDays(-3)]);
        Assert.Equal(0, result[date.AddDays(-4)]);
        Assert.Equal(0, result[date.AddDays(-5)]);
        Assert.Equal(0, result[date.AddDays(-6)]);
    }

    [Fact, CleanDatabase]
    public void DeletedByDatesCount_ReturnsEntriesForTheWholeWeek_EvenWhenThereAreNoDeletedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.DeletedByDatesCount();
        DateTime date = _utcNow.Date;

        Assert.Equal(7, result.Count);
        Assert.Equal(0, result[date]);
        Assert.Equal(0, result[date.AddDays(-1)]);
        Assert.Equal(0, result[date.AddDays(-2)]);
        Assert.Equal(0, result[date.AddDays(-3)]);
        Assert.Equal(0, result[date.AddDays(-4)]);
        Assert.Equal(0, result[date.AddDays(-5)]);
        Assert.Equal(0, result[date.AddDays(-6)]);
    }

    [Fact, CleanDatabase]
    public void HourlySucceededJobs_ReturnsEntriesForTheWholeDay_EvenWhenThereAreNoSucceededJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.HourlySucceededJobs();

        Assert.Equal(24, result.Count);
        Assert.Equal(0, result[result.Keys.ElementAt(0)]);
        Assert.Equal(0, result[result.Keys.ElementAt(3)]);
        Assert.Equal(0, result[result.Keys.ElementAt(6)]);
        Assert.Equal(0, result[result.Keys.ElementAt(9)]);
        Assert.Equal(0, result[result.Keys.ElementAt(12)]);
        Assert.Equal(0, result[result.Keys.ElementAt(15)]);
        Assert.Equal(0, result[result.Keys.ElementAt(18)]);
        Assert.Equal(0, result[result.Keys.ElementAt(21)]);
    }

    [Fact, CleanDatabase]
    public void HourlyFailedJobs_ReturnsEntriesForTheWholeDay_EvenWhenThereAreNoFailedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.HourlyFailedJobs();

        Assert.Equal(24, result.Count);
        Assert.Equal(0, result[result.Keys.ElementAt(0)]);
        Assert.Equal(0, result[result.Keys.ElementAt(3)]);
        Assert.Equal(0, result[result.Keys.ElementAt(6)]);
        Assert.Equal(0, result[result.Keys.ElementAt(9)]);
        Assert.Equal(0, result[result.Keys.ElementAt(12)]);
        Assert.Equal(0, result[result.Keys.ElementAt(15)]);
        Assert.Equal(0, result[result.Keys.ElementAt(18)]);
        Assert.Equal(0, result[result.Keys.ElementAt(21)]);
    }

    [Fact, CleanDatabase]
    public void HourlyDeletedJobs_ReturnsEntriesForTheWholeDay_EvenWhenThereAreNoDeletedJobs()
    {
        SqlServerMonitoringApi monitoring = CreateMonitoringApi();

        IDictionary<DateTime, long> result = monitoring.HourlyDeletedJobs();

        Assert.Equal(24, result.Count);
        Assert.Equal(0, result[result.Keys.ElementAt(0)]);
        Assert.Equal(0, result[result.Keys.ElementAt(3)]);
        Assert.Equal(0, result[result.Keys.ElementAt(6)]);
        Assert.Equal(0, result[result.Keys.ElementAt(9)]);
        Assert.Equal(0, result[result.Keys.ElementAt(12)]);
        Assert.Equal(0, result[result.Keys.ElementAt(15)]);
        Assert.Equal(0, result[result.Keys.ElementAt(18)]);
        Assert.Equal(0, result[result.Keys.ElementAt(21)]);
    }

    private string SimpleProcessingJob(string serverId = null, string workerId = null, Job job = null)
    {
        Type type = typeof(ProcessingState);
        ConstructorInfo[] ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        ProcessingState processingState = (ProcessingState)ctor.First().Invoke([serverId ?? "server-1", workerId ?? "worker-1"]);

        return SimpleJob(state: processingState, job: job);
    }

    private string SimpleScheduledJob(TimeSpan? delay = null, Job job = null)
    {
        ScheduledState state = new(delay ?? TimeSpan.FromHours(1));
        return SimpleJob(job: job, state: state, transactionAction: (transaction, assignedJobId, assignedJob) =>
        {
            transaction.AddToSet(
                "schedule",
                assignedJob.Queue != null ? $"{assignedJob.Queue}:{assignedJobId}" : assignedJobId,
                JobHelper.ToTimestamp(state.EnqueueAt));
        });
    }

    private string SimpleEnqueueJob(string queue, string jobId = null, IState state = null, Job job = null, bool noState = false)
    {
        return SimpleJob(jobId, job, !noState ? state ?? new EnqueuedState() : null, transactionAction: (transaction, assignedJobId, _) =>
        {
            transaction.AddToQueue(queue, assignedJobId);
        });
    }

    private string SimpleSucceededJob(object result = null, long latency = 0, long duration = 0, Job job = null, TimeSpan? expireIn = null)
    {
        return SimpleJob(
            job: job,
            state: new SucceededState(result, latency, duration),
            expireIn: expireIn);
    }

    private string SimpleFailedJob(Exception exception = null, Job job = null)
    {
        return SimpleJob(
            job: job,
            state: new FailedState(exception ?? new InvalidOperationException()));
    }

    private string SimpleDeletedJob(Exception exception = null, Job job = null, TimeSpan? expireIn = null)
    {
        return SimpleJob(
            job: job,
            state: new DeletedState(
                new ExceptionInfo(exception ?? new InvalidOperationException())
            ),
            expireIn: expireIn);
    }

    private string SimpleAwaitingJob(string parentId, Job job = null)
    {
        return SimpleJob(
            job: job,
            state: new AwaitingState(parentId));
    }

    private string SimpleJob(string jobId = null, Job job = null, IState state = null, TimeSpan? expireIn = null, Action<IWriteOnlyTransaction, string, Job> transactionAction = null)
    {
        string createdId = UseConnection(connection =>
        {
            job ??= Job.FromExpression(() => Empty());
            jobId ??= connection.CreateExpiredJob(
                job,
                new Dictionary<string, string>(),
                _utcNow,
                TimeSpan.FromMinutes(1));

            using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
            {
                if (state != null) transaction.SetJobState(jobId, state);
                transactionAction?.Invoke(transaction, jobId, job);
                if (expireIn != null) transaction.ExpireJob(jobId, expireIn.Value);
                transaction.Commit();
            }

            return jobId;
        });

        return createdId;
    }

    private SqlServerMonitoringApi CreateMonitoringApi()
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);
        return new SqlServerMonitoringApi(storage, 1_000);
    }

    private T UseConnection<T>(Func<SqlServerConnection, T> action)
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);
        using SqlServerConnection connection = new(storage);
        return action(connection);
    }

    private void UseSqlConnection(Action<DbConnection> action)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        action(connection);
    }

    private static void AssertWithinSecond(DateTime date1, DateTime? date2)
    {
        Assert.NotNull(date2);
        Assert.True((date1 - date2.Value).TotalSeconds <= 1.0D);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Empty()
    {
    }
}
