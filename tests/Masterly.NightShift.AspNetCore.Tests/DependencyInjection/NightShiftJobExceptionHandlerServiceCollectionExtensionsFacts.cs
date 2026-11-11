using System.Reflection;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests.DependencyInjection;

public class NightShiftJobExceptionHandlerServiceCollectionExtensionsFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly HandlerLog _log = new();

    [Fact]
    public void AddNightShiftJobExceptionHandler_RegistersTheHandler_AsScoped()
    {
        IServiceCollection services = new ServiceCollection().AddNightShiftJobExceptionHandler<RecordingHandler>();

        ServiceDescriptor descriptor = Assert.Single(services, x => x.ServiceType == typeof(IJobExceptionHandler));
        Assert.Equal(typeof(RecordingHandler), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddNightShiftJobExceptionHandler_AddsTheSameHandlerOnce()
    {
        IServiceCollection services = new ServiceCollection()
            .AddNightShiftJobExceptionHandler<RecordingHandler>()
            .AddNightShiftJobExceptionHandler<RecordingHandler>();

        Assert.Single(services, x => x.ServiceType == typeof(IJobExceptionHandler));
    }

    [Fact]
    public void AddNightShiftJobExceptionHandler_ThrowsAnException_WhenServicesIsNull()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(
            () => NightShiftJobExceptionHandlerServiceCollectionExtensions.AddNightShiftJobExceptionHandler<RecordingHandler>(null)).ParamName);
    }

    [Fact]
    public void Server_CallsEveryHandler_InANewScope_ForAFailedJob()
    {
        using ServiceProvider provider = BuildServices(services => services
            .AddNightShiftJobExceptionHandler<RecordingHandler>()
            .AddNightShiftJobExceptionHandler<SecondHandler>()).BuildServiceProvider();

        Assert.Throws<JobPerformanceException>(() => Perform(provider, Job.FromExpression<Jobs>(x => x.Fail())));
        Assert.Throws<JobPerformanceException>(() => Perform(provider, Job.FromExpression<Jobs>(x => x.Fail())));

        Assert.Equal(["Recording: Boom", "Second: Boom", "Recording: Boom", "Second: Boom"], _log.Entries);
        // A scope per call: each call gets new scoped instances
        Assert.Equal(2, _log.ScopedDependencies.Distinct().Count());
    }

    [Fact]
    public void Server_CallsTheOtherHandlers_WhenOneThrows()
    {
        using ServiceProvider provider = BuildServices(services => services
            .AddNightShiftJobExceptionHandler<ThrowingHandler>()
            .AddNightShiftJobExceptionHandler<RecordingHandler>()).BuildServiceProvider();

        // The job's own exception is reported; the handler's isn't
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform(provider, Job.FromExpression<Jobs>(x => x.Fail())));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(["Recording: Boom"], _log.Entries);
    }

    [Fact]
    public void Server_DoesNotCallHandlers_ForASucceededJob()
    {
        using ServiceProvider provider = BuildServices(services => services.AddNightShiftJobExceptionHandler<RecordingHandler>()).BuildServiceProvider();

        Perform(provider, Job.FromExpression<Jobs>(x => x.Succeed()));

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Server_KeepsTheFilterProvider_WhenNoHandlerIsRegistered()
    {
        using ServiceProvider provider = BuildServices(_ => { }).ConfigureNightShiftJobLogging(o => o.BeginScope = false).BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IJobFilterProvider>(), GetServerOptions(provider).FilterProvider);
    }

    [Fact]
    public void Server_AddsTheHandlersOnce_WhenSharedServerOptionsAreUsedTwice()
    {
        IServiceCollection services = BuildServices(s => s.AddNightShiftJobExceptionHandler<RecordingHandler>(), addServer: false)
            .AddSingleton(new BackgroundJobServerOptions())
            .AddNightShiftServer();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().ToList();
        provider.GetServices<IHostedService>().ToList();

        Assert.Throws<JobPerformanceException>(() => Perform(provider, Job.FromExpression<Jobs>(x => x.Fail())));

        Assert.Equal(["Recording: Boom"], _log.Entries);
    }

    private IServiceCollection BuildServices(Action<IServiceCollection> configure, bool addServer = true)
    {
        IServiceCollection services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(_log)
            .AddScoped<ScopedDependency>()
            .AddTransient<Jobs>()
            .AddSingleton(_storage.Object)
            .AddNightShift(_ => { });

        configure(services);

        return addServer ? services.AddNightShiftServer(_ => { }) : services;
    }

    private void Perform(IServiceProvider provider, Job job)
    {
        BackgroundJobServerOptions options = GetServerOptions(provider);
        BackgroundJobPerformer performer = new(options.FilterProvider, options.Activator, null);
        BackgroundJob backgroundJob = new("42", job, DateTime.UtcNow, new Dictionary<string, string>());
        Mock<IJobCancellationToken> token = new();
        token.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);

        performer.Perform(new PerformContext(_storage.Object, _connection.Object, backgroundJob, token.Object));
    }

    private static BackgroundJobServerOptions GetServerOptions(IServiceProvider provider)
    {
        BackgroundJobServerHostedService server = provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>().First();

        // The options are private to the hosted service
        return (BackgroundJobServerOptions)typeof(BackgroundJobServerHostedService)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(server);
    }

    public sealed class HandlerLog
    {
        public List<string> Entries { get; } = [];

        public List<ScopedDependency> ScopedDependencies { get; } = [];
    }

    public sealed class ScopedDependency;

    public sealed class RecordingHandler(HandlerLog log, ScopedDependency dependency) : IJobExceptionHandler
    {
        public void OnJobException(JobExceptionContext context)
        {
            log.Entries.Add($"Recording: {context.Exception.Message}");
            log.ScopedDependencies.Add(dependency);
        }

        public void OnJobCanceled(JobExceptionContext context) => log.Entries.Add("Recording: canceled");
    }

    public sealed class SecondHandler(HandlerLog log) : IJobExceptionHandler
    {
        public void OnJobException(JobExceptionContext context) => log.Entries.Add($"Second: {context.Exception.Message}");

        public void OnJobCanceled(JobExceptionContext context) => log.Entries.Add("Second: canceled");
    }

    public sealed class ThrowingHandler : IJobExceptionHandler
    {
        public void OnJobException(JobExceptionContext context) => throw new InvalidOperationException("handler bug");

        public void OnJobCanceled(JobExceptionContext context) => throw new InvalidOperationException("handler bug");
    }

    public sealed class Jobs
    {
        public void Fail() => throw new InvalidOperationException("Boom");

        public void Succeed()
        {
        }
    }
}
