using System.Text.Json;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public sealed class JobFunctionsFacts : IDisposable
{
    public JobFunctionsFacts()
    {
        JobFunctions.Clear();
        JobFunctions.Add<Maintenance>();
    }

    public void Dispose()
    {
        JobFunctions.Clear();
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void Add_RegistersTheMarkedMethods_ByName()
    {
        Assert.Equal(["maintenance.cleanup", "maintenance.ping", "maintenance.rename", "maintenance.report"], JobFunctions.GetAll().Select(static x => x.Name));
        Assert.Equal("Deletes old rows", JobFunctions.Find("Maintenance.Cleanup")?.Description);
        Assert.Equal(typeof(int), JobFunctions.Find("maintenance.cleanup")?.PayloadType);
        Assert.Null(JobFunctions.Find("maintenance.report")?.PayloadType);
        Assert.Null(JobFunctions.Find("unknown"));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void Add_ThrowsAnException_ForANameDeclaredTwice()
    {
        Assert.Throws<InvalidOperationException>(JobFunctions.Add<Duplicated>);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-bad")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void NormalizeName_RefusesInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>("name", () => JobFunctions.NormalizeName(name));
    }

    [Fact]
    public void NormalizeName_TrimsAValidName_AndRefusesALongOne()
    {
        Assert.Equal("reports:daily_v2.run-now", JobFunctions.NormalizeName(" reports:daily_v2.run-now "));
        Assert.Throws<ArgumentException>("name", () => JobFunctions.NormalizeName(new string('a', 101)));
        Assert.Throws<ArgumentNullException>("name", () => JobFunctions.NormalizeName(null));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void CreateJob_WithOneParameter_ConvertsThePayload()
    {
        Job fromInt = JobFunctions.Find("maintenance.cleanup")!.CreateJob(30, null);
        Job fromText = JobFunctions.Find("maintenance.cleanup")!.CreateJob("45", "critical");
        Job fromElement = JobFunctions.Find("maintenance.cleanup")!.CreateJob(JsonDocument.Parse("7").RootElement, null);

        Assert.Equal(typeof(Maintenance), fromInt.Type);
        Assert.Equal(nameof(Maintenance.Cleanup), fromInt.Method.Name);
        Assert.Equal([30, CancellationToken.None], fromInt.Args);
        Assert.Equal(45, fromText.Args[0]);
        Assert.Equal("critical", fromText.Queue);
        Assert.Equal(7, fromElement.Args[0]);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void CreateJob_WithSeveralParameters_BindsThePayloadsProperties_WithDefaults()
    {
        Job job = JobFunctions.Find("maintenance.report")!.CreateJob(new { Region = "eu", days = 3 }, null);
        Job withDefault = JobFunctions.Find("maintenance.report")!.CreateJob("{\"region\":\"us\"}", null);

        Assert.Equal(["eu", 3], job.Args);
        Assert.Equal(["us", 7], withDefault.Args);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void CreateJob_ThrowsAnException_ForAPayloadThatDoesNotFit()
    {
        JobFunction cleanup = JobFunctions.Find("maintenance.cleanup")!;
        JobFunction report = JobFunctions.Find("maintenance.report")!;
        JobFunction ping = JobFunctions.Find("maintenance.ping")!;

        Assert.Throws<ArgumentException>("payload", () => cleanup.CreateJob("not a number", null));
        Assert.Throws<ArgumentException>("payload", () => cleanup.CreateJob(null, null));
        Assert.Throws<ArgumentException>("payload", () => report.CreateJob(new { Region = "eu", Dayz = 3 }, null));
        Assert.Throws<ArgumentException>("payload", () => report.CreateJob(new { Days = 3 }, null));
        Assert.Throws<ArgumentException>("payload", () => report.CreateJob("[1, 2]", null));
        Assert.Throws<ArgumentException>("payload", () => report.CreateJob("{ not json", null));
        Assert.Throws<ArgumentException>("payload", () => report.CreateJob(null, null));
        Assert.Throws<ArgumentException>("payload", () => ping.CreateJob(1, null));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void CreateJob_AcceptsANullPayload_ForAReferenceType_AndNoneForAFunctionWithoutParameters()
    {
        Assert.Equal([null], JobFunctions.Find("maintenance.rename")!.CreateJob(null, null).Args);
        Assert.Empty(JobFunctions.Find("maintenance.ping")!.CreateJob(null, null).Args);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void Register_RefusesAnotherFunctionOfTheSameName_UntilItIsRemoved()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobFunctions.Register(new FixedFunction("maintenance.ping")));
        Assert.Contains("Maintenance.Ping", exception.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(JobFunctions.Add<Other>);

        Assert.True(JobFunctions.Remove("Maintenance.Ping"));
        JobFunctions.Register(new FixedFunction("maintenance.ping"));

        Assert.IsType<FixedFunction>(JobFunctions.Find("maintenance.ping"));
        Assert.True(JobFunctions.Remove("Maintenance.Ping"));
        Assert.False(JobFunctions.Remove("maintenance.ping"));
        Assert.Throws<ArgumentNullException>("function", () => JobFunctions.Register(null));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void Register_KeepsTheSameMethod_AndReplacesAFunctionOfTheSameClass()
    {
        JobFunction cleanup = JobFunctions.Find("maintenance.cleanup")!;

        Assert.Equal(["maintenance.cleanup", "maintenance.ping", "maintenance.rename", "maintenance.report"], JobFunctions.Add<Maintenance>().Order(StringComparer.Ordinal));
        JobFunctions.Register(new FixedFunction("fixed"));
        JobFunction second = new FixedFunction("fixed");
        JobFunctions.Register(second);

        Assert.Equal(cleanup.Name, JobFunctions.Find("maintenance.cleanup")!.Name);
        Assert.Same(second, JobFunctions.Find("fixed"));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void AddAssembly_RegistersTheTypesItCanLoad()
    {
        JobFunctions.Clear();

        Assert.Equal(["maintenance.cleanup", "maintenance.ping", "maintenance.rename", "maintenance.report"], JobFunctions.AddAssembly(new PartlyLoadableAssembly()).Order(StringComparer.Ordinal));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void CreateJob_WithAnObjectParameter_KeepsAValueTheJobCanStore()
    {
        JobFunctions.Add<Storing>();

        Job job = JobFunctions.Find("storing.put")!.CreateJob(new { Key = "a", Value = new { Size = 3 } }, null);

        string payload = InvocationData.SerializeJob(job).SerializePayload()!;
        Assert.Contains("Size", payload, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions"), CleanSerializerSettings]
    public void JsonTextMessage_ForAFunctionWithAnObjectParameter_DoesNotCreateTheTypeItNames()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        JobFunctions.Add<Untrusted>();
        Common.JsonConvertersFacts.Gadget.Constructed = 0;
        string gadget = typeof(Common.JsonConvertersFacts.Gadget).AssemblyQualifiedName!;
        string message = $"{{\"kind\":\"order\",\"data\":{{\"$type\":\"{gadget}\",\"Command\":\"calc.exe\"}}}}";

        Job job = JobFunctions.Find("untrusted.multi")!.CreateJob(message, null);
        Job back = InvocationData.SerializeJob(job).DeserializeJob();

        JsonElement data = Assert.IsType<JsonElement>(back.Args[1]);
        Assert.Equal(gadget, data.GetProperty("$type").GetString());
        Assert.Equal(0, Common.JsonConvertersFacts.Gadget.Constructed);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions"), CleanSerializerSettings]
    public void CreateJob_RefusesATypeName_InAPayloadMemberOfTypeObject()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        JobFunctions.Add<Untrusted>();
        Common.JsonConvertersFacts.Gadget.Constructed = 0;
        string gadget = typeof(Common.JsonConvertersFacts.Gadget).AssemblyQualifiedName!;
        string message = $"{{\"Name\":\"x\",\"Extra\":{{\"k\":{{\"$type\":\"{gadget}\",\"Command\":\"calc.exe\"}}}}}}";

        Assert.Throws<ArgumentException>("payload", () => JobFunctions.Find("untrusted.payload")!.CreateJob(message, null));
        Assert.Equal(0, Common.JsonConvertersFacts.Gadget.Constructed);
    }

    [Fact, GlobalLock(Reason = "Changes the global functions"), CleanSerializerSettings]
    public void CreateJob_ConvertsAnObjectPayload_WithObjectMembers_AsPlainJson()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        JobFunctions.Add<Untrusted>();

        Job job = JobFunctions.Find("untrusted.payload")!.CreateJob(
            new { Name = "x", Extra = new Dictionary<string, object> { ["n"] = 1, ["o"] = new UntrustedPayload { Name = "inner" } } },
            null);

        UntrustedPayload payload = Assert.IsType<UntrustedPayload>(job.Args[0]);
        Assert.Equal("x", payload.Name);
        Assert.Equal(1L, payload.Extra!["n"]);
        Assert.Equal("inner", Assert.IsType<JsonElement>(payload.Extra["o"]).GetProperty("Name").GetString());
    }

    [Fact]
    public void IsFunctionJob_MatchesMarkedMethods_AndDelegatesByName()
    {
        Job method = Job.FromExpression<Maintenance>(x => x.Ping());
        Job other = Job.FromExpression<Maintenance>(x => x.NotAFunction());
        Job function = Job.FromExpression<DelegateJobFunctionRunner>(x => x.RunAsync("Cache.Warm", null, CancellationToken.None));

        Assert.True(PendingJobs.IsFunctionJob(method, "maintenance.ping"));
        Assert.False(PendingJobs.IsFunctionJob(other, "maintenance.ping"));
        Assert.True(PendingJobs.IsFunctionJob(function, "cache.warm"));
        Assert.False(PendingJobs.IsFunctionJob(function, "cache.clear"));
        Assert.False(PendingJobs.IsFunctionJob(null, "cache.warm"));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void AddAssembly_ThrowsAnException_WhenATypeDeclaresANameTwice()
    {
        JobFunctions.Clear();

        Assert.Throws<InvalidOperationException>(() => JobFunctions.AddAssembly(typeof(Maintenance).Assembly));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void EnqueueFunction_CreatesTheJob_InTheEnqueuedState()
    {
        Mock<IBackgroundJobClient> client = new();
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");

        Assert.Equal("1", client.Object.EnqueueFunction("maintenance.cleanup", 30));
        client.Verify(x => x.Create(It.Is<Job>(job => job.Method.Name == nameof(Maintenance.Cleanup) && (int)job.Args[0] == 30), It.IsAny<EnqueuedState>()));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void ScheduleFunction_CreatesTheJob_InTheScheduledState_WithTheOptions()
    {
        Mock<IBackgroundJobClientV2> client = new();
        client.Setup(x => x.Storage).Returns(new Mock<JobStorage>().Object);
        DateTimeOffset at = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

        client.Object.ScheduleFunction("maintenance.cleanup", 30, at, new ScheduleOptions { Policy = "weekdays" });
        client.Object.ScheduleFunction("maintenance.cleanup", 30, TimeSpan.FromHours(1));

        client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(s => s.EnqueueAt == at.UtcDateTime), It.Is<IDictionary<string, object>>(p => (string)p["Policy"] == "weekdays")));
        client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void EnqueueFunction_ThrowsAnException_ForAnUnknownFunction_OrABadPayload()
    {
        IBackgroundJobClient client = new Mock<IBackgroundJobClient>().Object;

        Assert.Throws<ArgumentException>("name", () => client.EnqueueFunction("unknown"));
        Assert.Throws<ArgumentException>("payload", () => client.EnqueueFunction("maintenance.cleanup", "x"));
        Assert.Throws<NotSupportedException>(() => client.EnqueueFunction("maintenance.cleanup", 1, new ScheduleOptions()));
    }

    [Fact, GlobalLock(Reason = "Changes the global functions")]
    public void AddOrUpdateFunction_RegistersTheRecurringJob_WithTheCheckedPayload()
    {
        Mock<IRecurringJobManager> manager = new();

        manager.Object.AddOrUpdateFunction("nightly-cleanup", "maintenance.cleanup", 90, "0 3 * * *");

        manager.Verify(x => x.AddOrUpdate("nightly-cleanup", It.Is<Job>(job => (int)job.Args[0] == 90), "0 3 * * *", It.IsAny<RecurringJobOptions>()));
        Assert.Throws<ArgumentException>("payload", () => manager.Object.AddOrUpdateFunction("x", "maintenance.cleanup", "no", "0 3 * * *"));
    }

    [Fact]
    public void GetPendingJobs_ListsEnqueuedScheduledAndAwaitingJobs()
    {
        Mock<JobStorage> storage = new();
        Mock<JobStorageMonitor> monitor = new();
        storage.Setup(x => x.GetMonitoringApi()).Returns(monitor.Object);
        storage.Setup(x => x.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs)).Returns(true);
        Job job = Job.FromExpression<Maintenance>(x => x.Ping());

        monitor.Setup(x => x.Queues()).Returns([new QueueWithTopEnqueuedJobsDto { Name = "default" }]);
        monitor.Setup(x => x.EnqueuedJobs("default", 0, 500)).Returns(new JobList<EnqueuedJobDto>([
            new("1", new EnqueuedJobDto { Job = job, InEnqueuedState = true }),
            new("2", new EnqueuedJobDto { Job = job, InEnqueuedState = false }),
        ]));
        monitor.Setup(x => x.ScheduledJobs(0, 500)).Returns(new JobList<ScheduledJobDto>([
            new("3", new ScheduledJobDto { Job = job, InScheduledState = true, EnqueueAt = new DateTime(2030, 1, 1) }),
        ]));
        monitor.Setup(x => x.AwaitingJobs(0, 500)).Returns(new JobList<AwaitingJobDto>([
            new("4", new AwaitingJobDto { Job = job, InAwaitingState = true }),
        ]));

        IReadOnlyList<PendingJob> pending = storage.Object.GetPendingJobs();

        Assert.Equal(["1", "3", "4"], pending.Select(static x => x.Id));
        Assert.Equal("default", pending[0].Queue);
        Assert.Equal(new DateTime(2030, 1, 1), pending[1].EnqueueAt);
        Assert.Equal(AwaitingState.StateName, pending[2].State);
    }

    public sealed class Maintenance
    {
        [JobFunction("maintenance.cleanup", Description = "Deletes old rows")]
        public void Cleanup(int days, CancellationToken cancellationToken)
        {
            _ = days;
            _ = cancellationToken;
        }

        [JobFunction("maintenance.report")]
        public void Report(string region, int days = 7)
        {
            _ = region;
            _ = days;
        }

        [JobFunction("maintenance.ping")]
        public void Ping()
        {
        }

        [JobFunction("maintenance.rename")]
        public void Rename(string newName)
        {
            _ = newName;
        }

        public void NotAFunction()
        {
        }
    }

    public sealed class Duplicated
    {
        [JobFunction("same")]
        public void First()
        {
        }

        [JobFunction("SAME")]
        public void Second()
        {
        }
    }

    public sealed class Other
    {
        [JobFunction("maintenance.ping")]
        public void Ping()
        {
        }
    }

    public sealed class Storing
    {
        [JobFunction("storing.put")]
        public void Put(string key, object value)
        {
            _ = key;
            _ = value;
        }
    }

    public sealed class Untrusted
    {
        [JobFunction("untrusted.multi")]
        public void Run(string kind, object data)
        {
            _ = kind;
            _ = data;
        }

        [JobFunction("untrusted.payload")]
        public void RunPayload(UntrustedPayload payload)
        {
            _ = payload;
        }
    }

    public sealed class UntrustedPayload
    {
        public string? Name { get; set; }
        public Dictionary<string, object>? Extra { get; set; }
    }

    /// <summary>An assembly one of whose types can't be loaded, as when a dependency is missing.</summary>
    private sealed class PartlyLoadableAssembly : System.Reflection.Assembly
    {
        public override Type[] GetTypes() => throw new System.Reflection.ReflectionTypeLoadException([typeof(Maintenance), null], [new FileNotFoundException("Missing.dll")]);

        public override System.Reflection.AssemblyName GetName() => new("PartlyLoadable");
    }

    private sealed class FixedFunction(string name) : JobFunction(name)
    {
        public override Type PayloadType => null;

        public override Job CreateJob(object payload, string queue) => Job.FromExpression<Maintenance>(x => x.Ping());
    }
}
