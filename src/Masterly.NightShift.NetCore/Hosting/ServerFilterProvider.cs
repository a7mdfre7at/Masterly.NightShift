using System.Runtime.CompilerServices;
using Masterly.NightShift.Common;

namespace Masterly.NightShift;

/// <summary>
/// A filter provider that puts one global filter in front of the filters of another provider. Servers added with
/// <c>AddNightShiftServer</c> get their logging scope and their job exception handlers this way, so they apply to
/// that server only.
/// </summary>
internal sealed class ServerFilterProvider(IJobFilterProvider inner, object filter) : IJobFilterProvider
{
    private readonly JobFilter _filter = new(filter, JobFilterScope.Global, null);
    private readonly ConditionalWeakTable<JobFilter[], JobFilter[]> _combined = [];

    /// <summary>Gets the provider whose filters follow the added one.</summary>
    public IJobFilterProvider Inner { get; } = inner;

    /// <summary>Gets the added filter.</summary>
    public object Filter => _filter.Instance;

    /// <summary>
    /// Tells whether <paramref name="provider"/>, or a provider it wraps, already adds a filter that
    /// <paramref name="match"/> accepts, so options passed to a server factory twice don't add it twice.
    /// </summary>
    public static bool Adds([CanBeNull] IJobFilterProvider? provider, [NotNull] Func<object, bool> match)
    {
        for (IJobFilterProvider? current = provider; current is ServerFilterProvider added; current = added.Inner)
        {
            if (match(added.Filter))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<JobFilter> GetFilters(Job? job)
    {
        IEnumerable<JobFilter> filters = Inner.GetFilters(job);

        // The default providers return one shared array per job method, so the combined list is kept per array
        return filters is JobFilter[] shared
            ? _combined.GetValue(shared, Combine)
            : Combine(filters);
    }

    // First, so the filter also covers the other server filters and what they log
    private JobFilter[] Combine(IEnumerable<JobFilter> filters) => [_filter, .. filters];
}
