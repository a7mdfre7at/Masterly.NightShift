using System.Collections.Concurrent;
using System.Reflection;

namespace Masterly.NightShift.Common;

/// <summary>
/// Turns the <see cref="Task{TResult}"/> of <see cref="object"/> an asynchronous interceptor produces
/// into a value of the job method's return type, so the performer can treat it like the method's own result.
/// </summary>
internal static class TaskShapeAdapter
{
    private static readonly ConcurrentDictionary<Type, Func<Task<object?>, object>> Adapters = new();

    private static readonly MethodInfo ToTaskOfMethod = typeof(TaskShapeAdapter)
        .GetTypeInfo().GetDeclaredMethod(nameof(ToTaskOf))!;

    public static object? Adapt(Task<object?> source, Type returnType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(returnType);

        if (returnType == typeof(void) || !IsTaskLikeType(returnType))
        {
            // A synchronous method: the caller expects the value itself.
            return source.GetAwaiter().GetResult();
        }

        return Adapters.GetOrAdd(returnType, static type => CreateAdapter(type))(source);
    }

    private static bool IsTaskLikeType(Type type)
    {
        if (typeof(Task).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo()))
        {
            return true;
        }

        return type.FullName != null && type.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);
    }

    private static Func<Task<object?>, object> CreateAdapter(Type returnType)
    {
        TypeInfo typeInfo = returnType.GetTypeInfo();

        if (returnType == typeof(Task) || returnType == typeof(Task<object?>))
        {
            return static source => source;
        }

        if (typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type resultType = typeInfo.GenericTypeArguments[0];
            MethodInfo toTaskOf = ToTaskOfMethod.MakeGenericMethod(resultType);
            Func<Task<object?>, Task> converter = toTaskOf.CreateDelegate<Func<Task<object?>, Task>>();

            return source => converter(source);
        }

        // ValueTask and ValueTask<T>: both have a constructor taking the corresponding Task.
        if (typeInfo.IsGenericType)
        {
            Type resultType = typeInfo.GenericTypeArguments[0];
            Type taskOfResult = typeof(Task<>).MakeGenericType(resultType);
            ConstructorInfo? constructor = typeInfo.GetConstructor([taskOfResult]);

            if (constructor != null)
            {
                MethodInfo toTaskOf = ToTaskOfMethod.MakeGenericMethod(resultType);
                Func<Task<object?>, Task> converter = toTaskOf.CreateDelegate<Func<Task<object?>, Task>>();

                return source => constructor.Invoke([converter(source)]);
            }
        }
        else
        {
            ConstructorInfo? constructor = typeInfo.GetConstructor([typeof(Task)]);
            if (constructor != null)
            {
                return source => constructor.Invoke([source]);
            }
        }

        throw new NotSupportedException($"Asynchronous interceptors can't adapt their result to the `{returnType}` return type.");
    }

    private static async Task<T> ToTaskOf<T>(Task<object?> source)
    {
        object? result = await source.ConfigureAwait(false);
        return result == null ? default! : (T)result;
    }
}
