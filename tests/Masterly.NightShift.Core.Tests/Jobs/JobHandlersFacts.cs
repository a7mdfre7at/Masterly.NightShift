using System.Reflection;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class JobHandlersFacts : IDisposable
{
    private readonly Mock<IBackgroundJobClient> _client = new();

    public JobHandlersFacts()
    {
        JobHandlers.Clear();
    }

    public void Dispose()
    {
        JobHandlers.Clear();
    }

    [Fact]
    public void Add_ThrowsAnException_WhenTheTypeIsNotAHandler()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(JobHandlers.Add<NotAHandler>);

        Assert.Contains("IJobHandler", exception.Message);
    }

    [Fact]
    public void Add_ThrowsAnException_WhenTheHandlerIsAbstract()
    {
        Assert.Throws<ArgumentException>(JobHandlers.Add<AbstractHandler>);
    }

    [Fact]
    public void Add_ThrowsAnException_WhenTheHandlerIsAnOpenGeneric()
    {
        Assert.Throws<ArgumentException>(() => JobHandlers.Add(typeof(GenericHandler<>)));
    }

    [Fact]
    public void Add_ThrowsAnException_WhenTheInterfaceIsImplementedExplicitly()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(JobHandlers.Add<ExplicitHandler>);

        Assert.Contains("public method", exception.Message);
    }

    [Fact]
    public void Add_KeepsBothHandlers_WhenTheyShareTheArguments()
    {
        JobHandlers.Add<InvoiceHandler>();

        JobHandlers.Add<OtherInvoiceHandler>();

        Assert.Equal([typeof(InvoiceHandler), typeof(OtherInvoiceHandler)], JobHandlers.GetHandlerTypes(typeof(SendInvoice)));
    }

    [Fact]
    public void Add_AcceptsAJobWithoutArguments()
    {
        JobHandlers.Add<NightlyJob>();

        Assert.Empty(JobHandlers.GetHandlerTypes(typeof(NightlyJob)));
    }

    [Fact]
    public void Add_ThrowsAnException_WhenAJobIsImplementedExplicitly()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(JobHandlers.Add<ExplicitJob>);

        Assert.Contains("public method", exception.Message);
    }

    [Fact]
    public void Add_ThrowsAnException_WhenAJobIsAbstract()
    {
        Assert.Throws<ArgumentException>(JobHandlers.Add<AbstractJob>);
    }

    [Fact]
    public void Add_DoesNotThrow_WhenTheSameHandlerIsAddedTwice()
    {
        JobHandlers.Add<InvoiceHandler>();

        JobHandlers.Add<InvoiceHandler>();

        Assert.Equal([typeof(InvoiceHandler)], JobHandlers.GetHandlerTypes(typeof(SendInvoice)));
    }

    [Fact]
    public void Add_RegistersEveryArgumentsType_OfAHandlerWithSeveralInterfaces()
    {
        JobHandlers.Add<TwoArgumentsHandler>();

        Assert.Equal([typeof(TwoArgumentsHandler)], JobHandlers.GetHandlerTypes(typeof(FirstArgs)));
        Assert.Equal([typeof(TwoArgumentsHandler)], JobHandlers.GetHandlerTypes(typeof(SecondArgs)));
    }

    [Fact]
    public void GetHandlerTypes_ReturnsNothing_WhenNoHandlerIsRegistered()
    {
        Assert.Empty(JobHandlers.GetHandlerTypes(typeof(SendInvoice)));
    }

    [Fact]
    public void FindHandlers_ReturnsOnlyConcreteClosedHandlers()
    {
        IReadOnlyList<Type> handlers = JobHandlers.FindHandlers(typeof(JobHandlersFacts).Assembly);

        Assert.Contains(typeof(InvoiceHandler), handlers);
        Assert.Contains(typeof(NightlyJob), handlers);
        Assert.DoesNotContain(typeof(AbstractJob), handlers);
        Assert.DoesNotContain(typeof(AbstractHandler), handlers);
        Assert.DoesNotContain(typeof(GenericHandler<>), handlers);
        Assert.DoesNotContain(typeof(NotAHandler), handlers);
    }

    [Fact]
    public void CreateJob_ThrowsAnException_NamingTheArguments_WhenNoHandlerIsRegistered()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobHandlers.CreateJob(new SendInvoice(1)));

        Assert.Contains(nameof(SendInvoice), exception.Message);
    }

    [Fact]
    public void CreateJob_ThrowsAnException_NamingTheHandlers_WhenTheArgumentsHaveSeveral()
    {
        JobHandlers.Add<InvoiceHandler>();
        JobHandlers.Add<OtherInvoiceHandler>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => JobHandlers.CreateJob(new SendInvoice(1)));

        Assert.Contains(nameof(InvoiceHandler), exception.Message);
        Assert.Contains(nameof(OtherInvoiceHandler), exception.Message);
    }

    [Fact]
    public void CreateJob_ThrowsAnException_WhenArgumentsAreNull()
    {
        JobHandlers.Add<InvoiceHandler>();

        Assert.Throws<ArgumentNullException>(() => JobHandlers.CreateJob<SendInvoice>(null));
    }

    [Fact]
    public void CreateJob_TargetsTheHandlerClassItself_NotAnAdapter()
    {
        JobHandlers.Add<InvoiceHandler>();

        Job job = JobHandlers.CreateJob(new SendInvoice(7));

        Assert.Equal(typeof(InvoiceHandler), job.Type);
        Assert.Equal(typeof(InvoiceHandler), job.Method.DeclaringType);
        Assert.Equal(new SendInvoice(7), job.Args[0]);
    }

    [Fact]
    public void CreateJob_FiltersOnTheHandlerClassAndMethodApply()
    {
        JobHandlers.Add<InvoiceHandler>();
        Job job = JobHandlers.CreateJob(new SendInvoice(7));

        Type[] filters = [.. new JobFilterAttributeFilterProvider().GetFilters(job).Select(static x => x.Instance.GetType())];

        Assert.Contains(typeof(QueueAttribute), filters);
        Assert.Contains(typeof(AutomaticRetryAttribute), filters);
    }

    [Fact]
    public void CreateJob_RoundTripsThroughStorageFormat_WithTheArguments()
    {
        JobHandlers.Add<InvoiceHandler>();
        Job job = JobHandlers.CreateJob(new SendInvoice(7));

        Job restored = InvocationData.SerializeJob(job).DeserializeJob();

        Assert.Equal(typeof(InvoiceHandler), restored.Type);
        Assert.Equal(job.Method, restored.Method);
        Assert.Equal(new SendInvoice(7), restored.Args[0]);
    }

    [Fact]
    public void CreateJob_UsesAHandleMethodInheritedFromABaseClass()
    {
        JobHandlers.Add<DerivedHandler>();

        Job job = JobHandlers.CreateJob(new DerivedArgs());

        Assert.Equal(typeof(DerivedHandler), job.Type);
        Assert.Equal(typeof(BaseHandler), job.Method.DeclaringType);
    }

    [Fact]
    public void CreateJob_WithAChosenHandler_TargetsIt_WithoutARegistration()
    {
        Job job = JobHandlers.CreateJob<OtherInvoiceHandler, SendInvoice>(new SendInvoice(7));

        Assert.Equal(typeof(OtherInvoiceHandler), job.Type);
        Assert.Equal(new SendInvoice(7), job.Args[0]);
    }

    [Fact]
    public void CreateJob_WithAChosenHandler_WorksWhenSeveralAreRegistered()
    {
        JobHandlers.Add<InvoiceHandler>();
        JobHandlers.Add<OtherInvoiceHandler>();

        Job job = JobHandlers.CreateJob<InvoiceHandler, SendInvoice>(new SendInvoice(7));

        Assert.Equal(typeof(InvoiceHandler), job.Type);
    }

    [Fact]
    public void CreateJob_WithAChosenHandler_RoundTripsThroughStorageFormat()
    {
        Job restored = InvocationData.SerializeJob(JobHandlers.CreateJob<OtherInvoiceHandler, SendInvoice>(new SendInvoice(7))).DeserializeJob();

        Assert.Equal(typeof(OtherInvoiceHandler), restored.Type);
        Assert.Equal(new SendInvoice(7), restored.Args[0]);
    }

    [Fact]
    public void CreateJob_WithAChosenHandler_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => JobHandlers.CreateJob<InvoiceHandler, SendInvoice>(null));
    }

    [Fact]
    public void CreateJob_WithAChosenHandler_ThrowsAnException_WhenItIsAbstract()
    {
        Assert.Throws<ArgumentException>(() => JobHandlers.CreateJob<AbstractHandler, OtherArgs>(new OtherArgs(1)));
    }

    [Fact]
    public void CreateJob_ForAJobClass_TargetsTheClassItself()
    {
        Job job = JobHandlers.CreateJob<NightlyJob>();

        Assert.Equal(typeof(NightlyJob), job.Type);
        Assert.Equal(typeof(NightlyJob), job.Method.DeclaringType);
    }

    [Fact]
    public void CreateJob_ForAJobClass_RoundTripsThroughStorageFormat()
    {
        Job restored = InvocationData.SerializeJob(JobHandlers.CreateJob<NightlyJob>()).DeserializeJob();

        Assert.Equal(typeof(NightlyJob), restored.Type);
        Assert.Equal(nameof(NightlyJob.RunAsync), restored.Method.Name);
    }

    [Fact]
    public void CreateJob_ForAJobClass_FiltersOnTheClassApply()
    {
        Job job = JobHandlers.CreateJob<NightlyJob>();

        Type[] filters = [.. new JobFilterAttributeFilterProvider().GetFilters(job).Select(static x => x.Instance.GetType())];

        Assert.Contains(typeof(QueueAttribute), filters);
    }

    [Fact]
    public void CreateJob_ForAJobClass_ThrowsAnException_WhenItIsAbstract()
    {
        Assert.Throws<ArgumentException>(JobHandlers.CreateJob<AbstractJob>);
    }

    [Fact]
    public void GeneratedInvoker_IsRegistered_ForAJobClassNoLambdaMentions()
    {
        MethodInfo method = typeof(NightlyJob).GetMethod(nameof(NightlyJob.RunAsync))!; // Declared below

        Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(method, out JobInvoker? invoker));
        Assert.Equal(JobInvokerKind.Generated, invoker.Kind);
    }

    [Fact]
    public void GeneratedInvoker_IsRegistered_ForAHandlerNoLambdaMentions()
    {
        MethodInfo method = typeof(InvoiceHandler).GetMethod(nameof(InvoiceHandler.HandleAsync))!; // Declared below

        Assert.True(JobInvokerRegistry.TryGetGeneratedInvoker(method, out JobInvoker? invoker));
        Assert.Equal(JobInvokerKind.Generated, invoker.Kind);
    }

    [Fact]
    public void Enqueue_ThrowsAnException_WhenClientIsNull()
    {
        JobHandlers.Add<InvoiceHandler>();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => BackgroundJobClientHandlerExtensions.Enqueue(null, new SendInvoice(1)));

        Assert.Equal("client", exception.ParamName);
    }

    [Fact]
    public void Enqueue_CreatesAJobForTheHandler_InTheEnqueuedState()
    {
        JobHandlers.Add<InvoiceHandler>();

        _client.Object.Enqueue(new SendInvoice(1));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(InvoiceHandler)), It.IsAny<EnqueuedState>()));
    }

    [Fact]
    public void Schedule_WithADelay_CreatesAJobInTheScheduledState()
    {
        JobHandlers.Add<InvoiceHandler>();

        _client.Object.Schedule(new SendInvoice(1), TimeSpan.FromMinutes(5));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(InvoiceHandler)), It.IsAny<ScheduledState>()));
    }

    [Fact]
    public void Schedule_AtAMoment_CreatesAJobScheduledForThatMoment()
    {
        JobHandlers.Add<InvoiceHandler>();
        DateTimeOffset moment = new(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        _client.Object.Schedule(new SendInvoice(1), moment);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(state => state.EnqueueAt == moment.UtcDateTime)));
    }

    [Fact]
    public void ContinueJobWith_CreatesAJobAwaitingItsParent()
    {
        JobHandlers.Add<InvoiceHandler>();

        _client.Object.ContinueJobWith("parent", new SendInvoice(1));

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<AwaitingState>(state => state.ParentId == "parent")));
    }

    [Fact]
    public void Enqueue_WithAChosenHandler_CreatesAJobForIt()
    {
        _client.Object.Enqueue<OtherInvoiceHandler, SendInvoice>(new SendInvoice(1));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(OtherInvoiceHandler)), It.IsAny<EnqueuedState>()));
    }

    [Fact]
    public void Schedule_WithAChosenHandler_AndADelay_CreatesAScheduledJob()
    {
        _client.Object.Schedule<OtherInvoiceHandler, SendInvoice>(new SendInvoice(1), TimeSpan.FromMinutes(5));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(OtherInvoiceHandler)), It.IsAny<ScheduledState>()));
    }

    [Fact]
    public void Schedule_WithAChosenHandler_AtAMoment_CreatesAJobForThatMoment()
    {
        DateTimeOffset moment = new(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        _client.Object.Schedule<OtherInvoiceHandler, SendInvoice>(new SendInvoice(1), moment);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(state => state.EnqueueAt == moment.UtcDateTime)));
    }

    [Fact]
    public void ContinueJobWith_WithAChosenHandler_CreatesAJobAwaitingItsParent()
    {
        _client.Object.ContinueJobWith<OtherInvoiceHandler, SendInvoice>("parent", new SendInvoice(1));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(OtherInvoiceHandler)), It.Is<AwaitingState>(state => state.ParentId == "parent")));
    }

    [Fact]
    public void Enqueue_ForAJobClass_CreatesAJobInTheEnqueuedState()
    {
        _client.Object.Enqueue<NightlyJob>();

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(NightlyJob)), It.IsAny<EnqueuedState>()));
    }

    [Fact]
    public void Enqueue_ForAJobClass_ThrowsAnException_WhenClientIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => BackgroundJobClientHandlerExtensions.Enqueue<NightlyJob>(null));

        Assert.Equal("client", exception.ParamName);
    }

    [Fact]
    public void Schedule_ForAJobClass_WithADelay_CreatesAScheduledJob()
    {
        _client.Object.Schedule<NightlyJob>(TimeSpan.FromMinutes(5));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(NightlyJob)), It.IsAny<ScheduledState>()));
    }

    [Fact]
    public void Schedule_ForAJobClass_AtAMoment_CreatesAJobForThatMoment()
    {
        DateTimeOffset moment = new(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        _client.Object.Schedule<NightlyJob>(moment);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(state => state.EnqueueAt == moment.UtcDateTime)));
    }

    [Fact]
    public void ContinueJobWith_ForAJobClass_CreatesAJobAwaitingItsParent()
    {
        _client.Object.ContinueJobWith<NightlyJob>("parent");

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(NightlyJob)), It.Is<AwaitingState>(state => state.ParentId == "parent")));
    }

    [Fact]
    public void LambdaOverloads_StillBind_NextToTheArgumentsOverload()
    {
        _client.Object.Enqueue<InvoiceHandler>(x => x.HandleAsync(new SendInvoice(1), null, CancellationToken.None));

        _client.Verify(x => x.Create(It.Is<Job>(job => job.Type == typeof(InvoiceHandler)), It.IsAny<EnqueuedState>()));
    }

    public sealed record SendInvoice(int InvoiceId);

    public sealed record OtherArgs(int Value);

    public sealed record FirstArgs;

    public sealed record SecondArgs;

    public sealed record DerivedArgs;

    public sealed class NotAHandler;

    [Queue("invoices")]
    public sealed class InvoiceHandler : IJobHandler<SendInvoice>
    {
        [AutomaticRetry(Attempts = 1)]
        public Task HandleAsync(SendInvoice args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class OtherInvoiceHandler : IJobHandler<SendInvoice>
    {
        public Task HandleAsync(SendInvoice args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public abstract class AbstractHandler : IJobHandler<OtherArgs>
    {
        public abstract Task HandleAsync(OtherArgs args, PerformContext context, CancellationToken cancellationToken);
    }

    public sealed class GenericHandler<T> : IJobHandler<T>
    {
        public Task HandleAsync(T args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class ExplicitHandler : IJobHandler<OtherArgs>
    {
        Task IJobHandler<OtherArgs>.HandleAsync(OtherArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class TwoArgumentsHandler : IJobHandler<FirstArgs>, IJobHandler<SecondArgs>
    {
        public Task HandleAsync(FirstArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task HandleAsync(SecondArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class BaseHandler : IJobHandler<DerivedArgs>
    {
        public Task HandleAsync(DerivedArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class DerivedHandler : BaseHandler;

    [Queue("nightly")]
    public sealed class NightlyJob : IJob
    {
        public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public abstract class AbstractJob : IJob
    {
        public abstract Task RunAsync(PerformContext context, CancellationToken cancellationToken);
    }

    public sealed class ExplicitJob : IJob
    {
        Task IJob.RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
