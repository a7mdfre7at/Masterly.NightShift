using System.ComponentModel;
using System.Reflection;
using Masterly.NightShift.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Masterly.NightShift;

/// <summary>
/// Registers <see cref="JobFunction"/>s in a <see cref="IServiceCollection"/>: methods marked with
/// <see cref="JobFunctionAttribute"/>, and delegates that run with the job's services.
/// </summary>
public static class NightShiftFunctionServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    extension([NotNull] IServiceCollection services)
    {
        /// <summary>
        /// Registers a delegate as a job function without a payload. The delegate runs in the job's own scope, with
        /// its services and its cancellation token. Register it in every application that creates or runs its jobs.
        /// Registering the same delegate again (another container built by the same code) changes nothing.
        /// </summary>
        /// <param name="name">The function's name.</param>
        /// <param name="handler">The work.</param>
        /// <returns>The service collection.</returns>
        /// <exception cref="InvalidOperationException">Another function is registered under this name.</exception>
        public IServiceCollection AddNightShiftFunction(
            [NotNull] string name,
            [NotNull] Func<IServiceProvider, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(handler);

            Register(new DelegateJobFunction(name, null, handler.Method, (provider, _, token) => handler(provider, token)));
            return services;
        }

        /// <summary>
        /// Registers a delegate as a job function with a payload of <typeparamref name="TPayload"/>, checked when a job is
        /// created and given to the delegate when it runs, in the job's own scope. Registering the same delegate again
        /// changes nothing.
        /// </summary>
        /// <typeparam name="TPayload">The payload's type.</typeparam>
        /// <param name="name">The function's name.</param>
        /// <param name="handler">The work.</param>
        /// <returns>The service collection.</returns>
        /// <exception cref="InvalidOperationException">Another function is registered under this name.</exception>
        public IServiceCollection AddNightShiftFunction<TPayload>(
            [NotNull] string name,
            [NotNull] Func<IServiceProvider, TPayload, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(handler);

            Register(new DelegateJobFunction(name, typeof(TPayload), handler.Method, (provider, payload, token) => handler(provider, (TPayload)payload!, token))); // Checked by the function
            return services;
        }

        /// <summary>
        /// Registers the methods marked with <see cref="JobFunctionAttribute"/> in these assemblies, and their types in
        /// the container as scoped services, unless they are registered already.
        /// </summary>
        /// <param name="assemblies">The assemblies to search, such as ones the host doesn't reference directly.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftFunctions([NotNull] params Assembly[] assemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(assemblies);

            foreach (Assembly assembly in assemblies)
            {
                JobFunctions.AddAssembly(assembly);

                // The classes whose instance methods are functions are created by the container; AddAssembly left out
                // the types that can't be loaded
                foreach (Type type in GetLoadableTypes(assembly).Where(static x => x is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
                {
                    if (type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(static x => x.IsDefined(typeof(JobFunctionAttribute), inherit: false)))
                    {
                        services.TryAddScoped(type);
                    }
                }
            }

            return services;
        }
    }

    // The same delegate (the same code) under the same name is registered once; another one is refused
    private static void Register(DelegateJobFunction function)
    {
        if (JobFunctions.Find(function.Name) is DelegateJobFunction registered &&
            (registered.Source != function.Source || registered.PayloadType != function.PayloadType))
        {
            throw new InvalidOperationException($"Job function '{function.Name}' is registered already, for another delegate. Use another name, or remove it first with JobFunctions.Remove.");
        }

        JobFunctions.Register(function);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}

/// <summary>A delegate registered as a job function; its jobs run <see cref="DelegateJobFunctionRunner"/>.</summary>
internal sealed class DelegateJobFunction(string name, Type? payloadType, MethodInfo source, Func<IServiceProvider, object?, CancellationToken, Task> handler) : JobFunction(name)
{
    public Func<IServiceProvider, object?, CancellationToken, Task> Handler { get; } = handler;

    /// <summary>The method of the delegate registered, the same for every registration made by the same code.</summary>
    public MethodInfo Source { get; } = source;

    public override Type? PayloadType { get; } = payloadType;

    public override Job CreateJob(object? payload, string? queue)
    {
        string? serialized = null;

        if (PayloadType == null)
        {
            if (payload != null) throw new ArgumentException($"Function '{Name}' takes no payload.", nameof(payload));
        }
        else
        {
            object? converted = ConvertPayload(payload, PayloadType);
            serialized = converted == null ? null : SerializationHelper.Serialize(converted, SerializationOption.User);
        }

        string name = Name;
        return Job.FromExpression<DelegateJobFunctionRunner>(x => x.RunAsync(name, serialized, CancellationToken.None), queue);
    }

    /// <summary>Reads a payload stored by <see cref="CreateJob"/>.</summary>
    public object? ReadPayload(string? serialized)
    {
        return PayloadType == null || serialized == null ? null : SerializationHelper.Deserialize(serialized, PayloadType, SerializationOption.User);
    }
}

/// <summary>Runs the jobs of delegates registered with <c>AddNightShiftFunction</c>. Not meant to be called directly.</summary>
/// <param name="services">The job's scope.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DelegateJobFunctionRunner([NotNull] IServiceProvider services)
{
    /// <summary>Runs the delegate registered under <paramref name="name"/>.</summary>
    /// <param name="name">The function's name.</param>
    /// <param name="payload">The payload, as JSON, or <c>null</c>.</param>
    /// <param name="cancellationToken">The job's cancellation token, passed by the server.</param>
    /// <exception cref="InvalidOperationException">No delegate is registered under this name in this application.</exception>
    [DisplayName("{0}")]
    public Task RunAsync([NotNull] string name, [CanBeNull] string? payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (JobFunctions.Find(name) is not DelegateJobFunction function)
        {
            throw new InvalidOperationException($"No delegate is registered as job function '{name}' in this application. Register it with AddNightShiftFunction on every server.");
        }

        return function.Handler(services, function.ReadPayload(payload), cancellationToken);
    }
}
