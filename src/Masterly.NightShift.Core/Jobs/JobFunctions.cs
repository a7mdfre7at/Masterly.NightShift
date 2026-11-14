using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Names a job method so it can be scheduled by name, with a payload, from code that doesn't reference its type:
/// <c>[JobFunction("maintenance.cleanup")]</c>, then <c>BackgroundJob.EnqueueFunction("maintenance.cleanup", new { Days = 30 })</c>.
/// Register the method's type with <see cref="JobFunctions.Add{T}"/> or its assembly with
/// <see cref="JobFunctions.AddAssembly"/>.
/// </summary>
/// <param name="name">The function's name, see <see cref="JobFunctions.NormalizeName"/>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class JobFunctionAttribute([NotNull] string name) : Attribute
{
    /// <summary>The function's name.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>What the function does, shown on the dashboard.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// A job that can be created by name: a method marked with <see cref="JobFunctionAttribute"/>, or a delegate
/// registered in a container. Register one with <see cref="JobFunctions.Register"/>.
/// </summary>
/// <param name="name">The function's name, see <see cref="JobFunctions.NormalizeName"/>.</param>
public abstract class JobFunction([NotNull] string name)
{
    /// <summary>The function's name, as given.</summary>
    public string Name { get; } = JobFunctions.NormalizeName(name);

    /// <summary>What the function does, or <c>null</c>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The type the payload is read as, or <c>null</c> when the function takes none or several parameters (a payload
    /// object whose properties are its parameters).
    /// </summary>
    public abstract Type? PayloadType { get; }

    /// <summary>
    /// Creates the job for a payload, checking it: a payload the function can't read throws, so a bad call fails
    /// where it is made and not when the job runs.
    /// </summary>
    /// <param name="payload">The payload, or <c>null</c>.</param>
    /// <param name="queue">The job's queue, or <c>null</c>.</param>
    /// <exception cref="ArgumentException">The payload doesn't fit the function.</exception>
    [NotNull]
    public abstract Job CreateJob([CanBeNull] object? payload, [CanBeNull] string? queue);

    /// <summary>
    /// Reads a payload as a type: an instance of it is used as is, a JSON text or <see cref="JsonElement"/> is read,
    /// and any other object is converted through JSON, with the application's serializer settings.
    /// </summary>
    /// <remarks>
    /// A payload may come from another service, a script or a message, so it is read as data, never as type
    /// names: a value declared as <see cref="object"/> (in a dictionary of objects, for example) is read as plain
    /// JSON, and a <c>"$type"</c> in it stays data (in the <see cref="JsonElement"/>) instead of creating the
    /// type it names; a <see cref="Type"/> value is refused; a value declared as an interface or abstract class takes
    /// a <c>"$type"</c> only when it names a loaded type assignable to it, as in the dashboard's forms. An object
    /// converted through JSON is written without type names, so its <see cref="object"/> members arrive as plain
    /// JSON too.
    /// </remarks>
    /// <exception cref="ArgumentException">The payload can't be read as the type.</exception>
    protected object? ConvertPayload([CanBeNull] object? payload, [NotNull] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (payload == null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            {
                throw new ArgumentException($"Function '{Name}' needs a payload of type {type.ToGenericTypeString()}.", nameof(payload));
            }

            return null;
        }

        if (type.IsInstanceOfType(payload))
        {
            return payload;
        }

        try
        {
            return SerializationHelper.DeserializeData(ToJson(payload), type);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or InvalidOperationException)
        {
            throw new ArgumentException($"The payload of function '{Name}' can't be read as {type.ToGenericTypeString()}: {ex.Message}", nameof(payload), ex);
        }
    }

    /// <summary>
    /// The payload as JSON: a text is taken as JSON already, and any other object is written with the application's
    /// serializer settings, without type names.
    /// </summary>
    protected static string ToJson([NotNull] object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => SerializationHelper.SerializeInput(payload, payload.GetType())!, // Not null: the payload isn't
        };
    }
}

/// <summary>The registered <see cref="JobFunction"/>s, by name.</summary>
public static class JobFunctions
{
    /// <summary>The longest function name.</summary>
    public const int MaxNameLength = 100;

    private static readonly ConcurrentDictionary<string, JobFunction> Functions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobFunctions));

    /// <summary>
    /// Checks a function name, 1 to 100 letters, digits, <c>.</c>, <c>_</c>, <c>-</c> and <c>:</c>, starting with a letter
    /// or a digit, and returns it trimmed. Names are compared without case.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't valid.</exception>
    public static string NormalizeName([NotNull] string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || !char.IsAsciiLetterOrDigit(trimmed[0]) ||
            !trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':'))
        {
            throw new ArgumentException(
                $"'{name}' isn't a valid function name: use 1 to {MaxNameLength} letters, digits, '.', '_', '-' and ':', starting with a letter or a digit.",
                nameof(name));
        }

        return trimmed;
    }

    /// <summary>
    /// Registers a function. Registering the same one again changes nothing: the same method, or a function of the
    /// same class, which replaces the one registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another function of the same name is registered: another method, or a function of another class. Remove it
    /// first to replace it.
    /// </exception>
    public static void Register([NotNull] JobFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        Functions.AddOrUpdate(
            function.Name,
            function,
            (name, registered) => IsSameFunction(registered, function)
                ? function
                : throw new InvalidOperationException($"Job function '{name}' is registered already, as {Describe(registered)}; it can't also be {Describe(function)}. Remove it first to replace it."));
    }

    private static bool IsSameFunction(JobFunction registered, JobFunction function)
    {
        if (ReferenceEquals(registered, function))
        {
            return true;
        }

        if (registered is MethodJobFunction method && function is MethodJobFunction other)
        {
            return method.Type == other.Type && method.Method == other.Method;
        }

        return registered.GetType() == function.GetType();
    }

    private static string Describe(JobFunction function)
    {
        return function is MethodJobFunction method
            ? $"{method.Type.ToGenericTypeString()}.{method.Method.Name}"
            : $"a {function.GetType().ToGenericTypeString()}";
    }

    /// <summary>Registers the methods of <typeparamref name="T"/> marked with <see cref="JobFunctionAttribute"/>.</summary>
    /// <returns>The names registered.</returns>
    public static IReadOnlyList<string> Add<T>() => Add(typeof(T));

    /// <summary>Registers the methods of a type marked with <see cref="JobFunctionAttribute"/>.</summary>
    /// <returns>The names registered.</returns>
    /// <exception cref="InvalidOperationException">A marked method can't be a job, two methods have the same name, or a name is registered already for another method.</exception>
    public static IReadOnlyList<string> Add([NotNull] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        List<MethodJobFunction> found = [];
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            JobFunctionAttribute? attribute = method.GetCustomAttribute<JobFunctionAttribute>();
            if (attribute == null)
            {
                continue;
            }

            if (!JobCatalog.IsRunnable(method))
            {
                throw new InvalidOperationException($"{type.ToGenericTypeString()}.{method.Name} can't be job function '{attribute.Name}': it can't be a job.");
            }

            found.Add(new MethodJobFunction(attribute.Name, type, method) { Description = attribute.Description });
        }

        foreach (IGrouping<string, MethodJobFunction> duplicate in found.GroupBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).Where(static x => x.Count() > 1))
        {
            throw new InvalidOperationException($"Job function '{duplicate.Key}' is declared more than once in {type.ToGenericTypeString()}.");
        }

        foreach (MethodJobFunction function in found)
        {
            Register(function);
        }

        return [.. found.Select(static x => x.Name)];
    }

    /// <summary>
    /// Registers the job functions of every type of an assembly. Types that can't be loaded (a dependency is
    /// missing) are left out, with a warning.
    /// </summary>
    /// <returns>The names registered.</returns>
    /// <exception cref="InvalidOperationException">A marked method can't be a job, a type declares a name twice, or a name is registered already for another method.</exception>
    public static IReadOnlyList<string> AddAssembly([NotNull] Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<string> names = [];
        foreach (Type type in GetLoadableTypes(assembly).Where(static x => x is { IsClass: true, IsGenericTypeDefinition: false }))
        {
            if (type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(static x => x.IsDefined(typeof(JobFunctionAttribute), inherit: false)))
            {
                names.AddRange(Add(type));
            }
        }

        return names;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Logger.WarnException($"Some types of assembly '{assembly.GetName().Name}' can't be loaded, so their job functions aren't registered.", ex);
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>Finds a function by name, or <c>null</c>.</summary>
    [CanBeNull]
    public static JobFunction? Find([NotNull] string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Functions.TryGetValue(name.Trim(), out JobFunction? function) ? function : null;
    }

    /// <summary>The registered functions, by name.</summary>
    public static IReadOnlyList<JobFunction> GetAll() => [.. Functions.Values.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Removes a function; <c>false</c> when there was none of that name.</summary>
    public static bool Remove([NotNull] string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Functions.TryRemove(name.Trim(), out _);
    }

    /// <summary>Creates the job of a function, checking the payload.</summary>
    /// <exception cref="ArgumentException">There is no function of this name, or the payload doesn't fit it.</exception>
    internal static Job CreateJob(string name, object? payload, string? queue)
    {
        ArgumentNullException.ThrowIfNull(name);

        JobFunction function = Find(name)
            ?? throw new ArgumentException($"There is no job function named '{name}'. Register it with JobFunctions.Add or AddNightShiftFunction.", nameof(name));
        return function.CreateJob(payload, queue);
    }

    // For tests
    internal static void Clear() => Functions.Clear();
}

/// <summary>A method marked with <see cref="JobFunctionAttribute"/>: the payload fills its parameters.</summary>
internal sealed class MethodJobFunction : JobFunction
{
    private readonly JobCatalogParameter[] _parameters;
    private readonly JobCatalogParameter[] _userParameters;

    public MethodJobFunction(string name, Type type, MethodInfo method)
        : base(name)
    {
        Type = type;
        Method = method;
        _parameters = [.. new JobCatalogEntry(type, method, null).Parameters];
        _userParameters = [.. _parameters.Where(static x => !x.Injected)];
    }

    /// <summary>The class whose method runs.</summary>
    public Type Type { get; }

    /// <summary>The method marked with <see cref="JobFunctionAttribute"/>.</summary>
    public MethodInfo Method { get; }

    public override Type? PayloadType => _userParameters.Length == 1 ? _userParameters[0].Type : null;

    public override Job CreateJob(object? payload, string? queue)
    {
        // Injected parameters (a cancellation token, the context) are replaced by the server; a value type needs a value
        object?[] args = [.. _parameters.Select(static x => x.Type.IsValueType ? Activator.CreateInstance(x.Type) : null)];

        if (_userParameters.Length == 1)
        {
            args[Array.IndexOf(_parameters, _userParameters[0])] = ConvertPayload(payload, _userParameters[0].Type);
        }
        else if (_userParameters.Length > 1)
        {
            BindProperties(payload, args);
        }
        else if (payload != null)
        {
            throw new ArgumentException($"Function '{Name}' takes no payload.", nameof(payload));
        }

        return new Job(Type, Method, args!, queue); // Null arguments are stored as null
    }

    // Several parameters: the payload's properties, by parameter name, without case
    private void BindProperties(object? payload, object?[] args)
    {
        if (payload == null)
        {
            throw new ArgumentException($"Function '{Name}' needs a payload with {string.Join(", ", _userParameters.Select(static x => x.Name))}.", nameof(payload));
        }

        using JsonDocument document = ParsePayload(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"The payload of function '{Name}' must be an object with {string.Join(", ", _userParameters.Select(static x => x.Name))}.", nameof(payload));
        }

        Dictionary<string, JsonElement> properties = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!_userParameters.Any(x => string.Equals(x.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Function '{Name}' has no parameter '{property.Name}'.", nameof(payload));
            }

            // A copy: the document is disposed on return, and an object parameter keeps the element itself
            properties[property.Name] = property.Value.Clone();
        }

        foreach (JobCatalogParameter parameter in _userParameters)
        {
            int index = Array.IndexOf(_parameters, parameter);
            if (properties.TryGetValue(parameter.Name, out JsonElement value))
            {
                args[index] = ConvertPayload(value, parameter.Type);
            }
            else if (parameter.HasDefaultValue)
            {
                args[index] = parameter.DefaultValue;
            }
            else
            {
                throw new ArgumentException($"The payload of function '{Name}' has no '{parameter.Name}'.", nameof(payload));
            }
        }
    }

    private JsonDocument ParsePayload(object payload)
    {
        try
        {
            return JsonDocument.Parse(ToJson(payload));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The payload of function '{Name}' isn't valid JSON: {ex.Message}", nameof(payload), ex);
        }
    }
}
