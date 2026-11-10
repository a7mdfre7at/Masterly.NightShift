using System.Reflection;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Knows which <see cref="IJobHandler{TArgs}"/> handles each arguments type, and builds the jobs that call job
/// classes. Handlers are added at start-up, before the first job is created. Several handlers can share an
/// arguments type; jobs for them name the handler, as in <see cref="CreateJob{THandler, TArgs}"/>.
/// </summary>
public static class JobHandlers
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobHandlers));
    private static readonly NightShiftLock SyncRoot = new();
    private static Dictionary<Type, Registration[]> _registrations = [];

    /// <summary>Checks a job class and adds it as a handler of every arguments type it handles.</summary>
    /// <typeparam name="THandler">A class implementing <see cref="IJob"/> or <see cref="IJobHandler{TArgs}"/>.</typeparam>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static void Add<THandler>()
        where THandler : class
    {
        Add(typeof(THandler));
    }

    /// <summary>Checks a job class and adds it as a handler of every arguments type it handles.</summary>
    /// <param name="handlerType">A class implementing <see cref="IJob"/> or <see cref="IJobHandler{TArgs}"/>.</param>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static void Add([NotNull] Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);

        List<(Type ArgsType, Registration Registration)> found = [.. Inspect(handlerType)];

        lock (SyncRoot)
        {
            Dictionary<Type, Registration[]> registrations = new(_registrations);

            foreach ((Type argsType, Registration registration) in found)
            {
                Registration[] existing = registrations.GetValueOrDefault(argsType, []);

                if (existing.All(candidate => candidate.HandlerType != handlerType))
                {
                    registrations[argsType] = [.. existing, registration];
                }
            }

            Volatile.Write(ref _registrations, registrations);
        }
    }

    /// <summary>
    /// Lists the concrete classes of an assembly that implement <see cref="IJob"/> or
    /// <see cref="IJobHandler{TArgs}"/>. Types that can't be loaded are skipped, with a warning in the log.
    /// </summary>
    /// <param name="assembly">The assembly to search.</param>
    /// <returns>The job classes, ordered by name.</returns>
    public static IReadOnlyList<Type> FindHandlers([NotNull] Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return [.. GetLoadableTypes(assembly)
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && IsJobClass(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)];
    }

    /// <summary>Finds the handlers of an arguments type.</summary>
    /// <param name="argsType">The exact arguments type.</param>
    /// <returns>The handler types, empty when none is registered.</returns>
    public static IReadOnlyList<Type> GetHandlerTypes([NotNull] Type argsType)
    {
        ArgumentNullException.ThrowIfNull(argsType);

        return [.. Volatile.Read(ref _registrations).GetValueOrDefault(argsType, []).Select(static registration => registration.HandlerType)];
    }

    /// <summary>
    /// Builds the job that passes <paramref name="args"/> to the only registered handler of
    /// <typeparamref name="TArgs"/>, for APIs that take a <see cref="Job"/>, such as <see cref="IRecurringJobManager"/>.
    /// </summary>
    /// <typeparam name="TArgs">The arguments type, which is the one the handler is looked up with.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <returns>The job.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TArgs"/> has no registered handler, or has several.
    /// </exception>
    public static Job CreateJob<TArgs>([NotNull] TArgs args)
        where TArgs : notnull
    {
        ArgumentNullException.ThrowIfNull(args);

        Registration registration = FindOnlyHandler(typeof(TArgs));
        return new Job(registration.HandlerType, registration.Method, CreateArguments(args));
    }

    /// <summary>Builds the job that passes <paramref name="args"/> to <typeparamref name="THandler"/>.</summary>
    /// <typeparam name="THandler">The handler. It doesn't need to be registered.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <returns>The job.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static Job CreateJob<THandler, TArgs>([NotNull] TArgs args)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        ArgumentNullException.ThrowIfNull(args);

        return new Job(typeof(THandler), GetImplementation(typeof(THandler), typeof(IJobHandler<TArgs>)), CreateArguments(args));
    }

    /// <summary>Builds the job that runs <typeparamref name="TJob"/>.</summary>
    /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
    /// <returns>The job.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static Job CreateJob<TJob>()
        where TJob : class, IJob
    {
        // The server replaces both arguments when the job runs, as it does for any job taking them
        object?[] arguments = [null, CancellationToken.None];
        return new Job(typeof(TJob), GetImplementation(typeof(TJob), typeof(IJob)), arguments!); // The context is deliberately null until the job runs
    }

    /// <summary>Removes every registered handler.</summary>
    public static void Clear()
    {
        lock (SyncRoot)
        {
            Volatile.Write(ref _registrations, []);
        }
    }

    private static object[] CreateArguments(object args)
    {
        // The server replaces the last two arguments when the job runs, as it does for any job taking them
        object?[] arguments = [args, null, CancellationToken.None];
        return arguments!; // The context is deliberately null until the job runs
    }

    private static Registration FindOnlyHandler(Type argsType)
    {
        Registration[] handlers = Volatile.Read(ref _registrations).GetValueOrDefault(argsType, []);

        return handlers switch
        {
            [Registration only] => only,
            [] => throw new InvalidOperationException(
                $"No job handler is registered for the arguments type '{argsType.ToGenericTypeString()}'. Add a class implementing IJobHandler<{argsType.Name}> with services.AddNightShiftJobHandler<THandler>() or JobHandlers.Add<THandler>()."),
            _ => throw new InvalidOperationException(
                $"The arguments type '{argsType.ToGenericTypeString()}' has several handlers ({string.Join(", ", handlers.Select(static handler => handler.HandlerType.ToGenericTypeString()))}). Name the one to run, as in Enqueue<THandler, {argsType.Name}>(args)."),
        };
    }

    private static IEnumerable<(Type ArgsType, Registration Registration)> Inspect(Type handlerType)
    {
        if (!IsJobClass(handlerType))
        {
            throw new ArgumentException($"The type '{handlerType.ToGenericTypeString()}' doesn't implement IJob or IJobHandler<TArgs>.", nameof(handlerType));
        }

        if (typeof(IJob).IsAssignableFrom(handlerType))
        {
            GetImplementation(handlerType, typeof(IJob));
        }

        return GetHandlerInterfaces(handlerType)
            .Select(handlerInterface => (handlerInterface.GetGenericArguments()[0], new Registration(handlerType, GetImplementation(handlerType, handlerInterface))));
    }

    private static MethodInfo GetImplementation(Type jobType, Type jobInterface)
    {
        if (!jobType.IsClass || jobType.IsAbstract || jobType.ContainsGenericParameters)
        {
            throw new ArgumentException($"The job class '{jobType.ToGenericTypeString()}' must be a concrete, closed class.", nameof(jobType));
        }

        // The class's own method, not the interface's: attributes on it are read from the method the job calls
        MethodInfo method = jobType.GetInterfaceMap(jobInterface).TargetMethods.Single();

        if (!method.IsPublic)
        {
            throw new ArgumentException(
                $"The job class '{jobType.ToGenericTypeString()}' must implement {jobInterface.Name} with a public method: an explicit interface implementation can't be a job.",
                nameof(jobType));
        }

        return method;
    }

    private static bool IsJobClass(Type type)
    {
        return typeof(IJob).IsAssignableFrom(type) || GetHandlerInterfaces(type).Any();
    }

    private static IEnumerable<Type> GetHandlerInterfaces(Type type)
    {
        return type.GetInterfaces().Where(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IJobHandler<>));
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Logger.WarnException($"Some types of the assembly '{assembly.GetName().Name}' can't be loaded, so job classes among them are not found.", ex);
            return [.. ex.Types.OfType<Type>()];
        }
    }

    private sealed record Registration(Type HandlerType, MethodInfo Method);
}
