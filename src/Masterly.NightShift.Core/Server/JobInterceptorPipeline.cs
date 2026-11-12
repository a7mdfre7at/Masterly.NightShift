using System.Collections.Concurrent;
using System.Reflection;

namespace Masterly.NightShift.Server;

/// <summary>
/// Finds the interceptors for a job: the global ones, then the attributes on its type and method,
/// ordered by <see cref="JobInterceptorAttribute.Order"/>. Attribute lookups are cached per method.
/// </summary>
internal static class JobInterceptorPipeline
{
    private static readonly IJobInterceptor[] Empty = [];

    private static readonly ConcurrentDictionary<(Type Type, MethodInfo Method), IJobInterceptor[]> AttributeCache = new();

    public static IReadOnlyList<IJobInterceptor> GetInterceptors(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        IJobInterceptor[] declared = AttributeCache.GetOrAdd((job.Type, job.Method), static key => GetDeclaredInterceptors(key.Type, key.Method));
        IReadOnlyList<IJobInterceptor> global = GlobalJobInterceptors.Interceptors;

        if (global.Count == 0)
        {
            return declared;
        }

        if (declared.Length == 0)
        {
            return global;
        }

        IJobInterceptor[] combined = new IJobInterceptor[global.Count + declared.Length];

        for (int i = 0; i < global.Count; i++)
        {
            combined[i] = global[i];
        }

        Array.Copy(declared, 0, combined, global.Count, declared.Length);
        return combined;
    }

    private static IJobInterceptor[] GetDeclaredInterceptors(Type type, MethodInfo method)
    {
        IEnumerable<JobInterceptorAttribute> typeAttributes = type.GetTypeInfo().GetCustomAttributes<JobInterceptorAttribute>(inherit: true);
        IEnumerable<JobInterceptorAttribute> methodAttributes = method.GetCustomAttributes<JobInterceptorAttribute>(inherit: true);

        IJobInterceptor[] all = [.. typeAttributes
            .Select(static (attribute, index) => (Attribute: attribute, Scope: 0, Index: index))
            .Concat(methodAttributes.Select(static (attribute, index) => (Attribute: attribute, Scope: 1, Index: index)))
            .OrderBy(static x => x.Attribute.Order)
            .ThenBy(static x => x.Scope)
            .ThenBy(static x => x.Index)
            .Select(static x => (IJobInterceptor)x.Attribute)];

        return all.Length == 0 ? Empty : all;
    }
}
