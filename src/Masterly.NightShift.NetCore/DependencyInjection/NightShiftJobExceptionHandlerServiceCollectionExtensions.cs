using Masterly.NightShift.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Masterly.NightShift;

/// <summary>
/// Registers <see cref="IJobExceptionHandler"/> implementations for the servers added with <c>AddNightShiftServer</c>.
/// </summary>
public static class NightShiftJobExceptionHandlerServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    extension([NotNull] IServiceCollection services)
    {
        /// <summary>
        /// Adds a job exception handler, created from the container in a scope of its own for each call, so it can
        /// depend on scoped services. It handles the jobs of the servers added with <c>AddNightShiftServer</c> in this
        /// service collection, whether added before or after this call. Several handlers can be added; adding the
        /// same one twice adds it once.
        /// </summary>
        /// <typeparam name="THandler">The handler.</typeparam>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftJobExceptionHandler<THandler>()
            where THandler : class, IJobExceptionHandler
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobExceptionHandler, THandler>());
            return services;
        }
    }
}

/// <summary>
/// Adds the filter that calls the job exception handlers registered in the container to a server's filter provider.
/// </summary>
internal static class ContainerJobExceptionHandlers
{
    /// <summary>
    /// Returns the filter provider a server created from <paramref name="services"/> should use: the given one with
    /// the handlers' filter in front of its filters, or the given one unchanged when no handler is registered.
    /// </summary>
    [CanBeNull]
    public static IJobFilterProvider? AddTo([CanBeNull] IJobFilterProvider? filterProvider, [NotNull] IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (ServerFilterProvider.Adds(filterProvider, static filter => filter is JobExceptionHandlerFilter { Handler: ScopedHandlers })) return filterProvider;

        IServiceProviderIsService? isService = services.GetService<IServiceProviderIsService>();
        if (isService == null || !isService.IsService(typeof(IJobExceptionHandler))) return filterProvider;

        IServiceScopeFactory scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

        return new ServerFilterProvider(
            filterProvider ?? JobFilterProviders.Providers,
            new JobExceptionHandlerFilter(new ScopedHandlers(scopeFactory)));
    }

    /// <summary>Calls every registered handler, each created in a scope of its own for the call.</summary>
    private sealed class ScopedHandlers(IServiceScopeFactory scopeFactory) : IJobExceptionHandler
    {
        public void OnJobException(JobExceptionContext context) => Call(context, static (handler, ctx) => handler.OnJobException(ctx));

        public void OnJobCanceled(JobExceptionContext context) => Call(context, static (handler, ctx) => handler.OnJobCanceled(ctx));

        private void Call(JobExceptionContext context, Action<IJobExceptionHandler, JobExceptionContext> call)
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            List<Exception>? errors = null;

            foreach (IJobExceptionHandler handler in scope.ServiceProvider.GetServices<IJobExceptionHandler>())
            {
                // One handler that throws doesn't keep the others from running
                try
                {
                    call(handler, context);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    (errors ??= []).Add(ex);
                }
            }

            if (errors != null)
            {
                // Logged by JobExceptionHandlerFilter, which never lets it reach the job
                throw new AggregateException("One or more job exception handlers threw.", errors);
            }
        }
    }
}
