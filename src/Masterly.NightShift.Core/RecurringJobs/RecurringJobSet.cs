using System.Linq.Expressions;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>What <see cref="RecurringJobSet.Apply(IRecurringJobManager, JobStorage)"/> did.</summary>
/// <param name="Applied">The identifiers of the recurring jobs registered or updated.</param>
/// <param name="Removed">The identifiers of the undeclared recurring jobs removed.</param>
public sealed record RecurringJobSetResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Removed);

/// <summary>
/// A set of recurring jobs declared in code and applied together at start-up. A declaration that can't work
/// (a bad CRON expression, a method that can't be a job, an identifier used twice) throws where it is made, so a
/// broken schedule stops the application instead of going unnoticed. Applying again changes nothing that
/// didn't change: an unchanged job keeps its next run and its paused state.
/// </summary>
/// <remarks>
/// With an application host, declare the jobs with <c>AddNightShiftRecurringJobs</c>, which applies them when
/// the host starts. Without one, call <see cref="Apply()"/>.
/// </remarks>
public sealed class RecurringJobSet
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(RecurringJobSet));

    private readonly List<Declaration> _declarations = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private bool _removeUndeclared;
    private Func<string, bool>? _removalScope;

    /// <summary>The identifiers declared so far.</summary>
    public IReadOnlyCollection<string> DeclaredIds => _ids;

    /// <summary>Declares a recurring job from a static method call.</summary>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="methodCall">The method to call.</param>
    /// <param name="cronExpression">The schedule.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add([NotNull] string recurringJobId, [NotNull, InstantHandle] Expression<Action> methodCall, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
    {
        return AddDeclaration(recurringJobId, () => Job.FromExpression(methodCall), cronExpression, options);
    }

    /// <summary>Declares a recurring job from a static asynchronous method call.</summary>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="methodCall">The method to call.</param>
    /// <param name="cronExpression">The schedule.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add([NotNull] string recurringJobId, [NotNull, InstantHandle] Expression<Func<Task>> methodCall, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
    {
        return AddDeclaration(recurringJobId, () => Job.FromExpression(methodCall), cronExpression, options);
    }

    /// <summary>Declares a recurring job that calls a method of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type the job's instance is created from.</typeparam>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="methodCall">The method to call.</param>
    /// <param name="cronExpression">The schedule.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add<T>([NotNull] string recurringJobId, [NotNull, InstantHandle] Expression<Action<T>> methodCall, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
    {
        return AddDeclaration(recurringJobId, () => Job.FromExpression(methodCall), cronExpression, options);
    }

    /// <summary>Declares a recurring job that calls an asynchronous method of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type the job's instance is created from.</typeparam>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="methodCall">The method to call.</param>
    /// <param name="cronExpression">The schedule.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add<T>([NotNull] string recurringJobId, [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
    {
        return AddDeclaration(recurringJobId, () => Job.FromExpression(methodCall), cronExpression, options);
    }

    /// <summary>Declares a recurring job run by the <see cref="IJobHandler{TArgs}"/> of <typeparamref name="TArgs"/>.</summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="args">The arguments every run gets.</param>
    /// <param name="cronExpression">The schedule.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet AddHandler<TArgs>([NotNull] string recurringJobId, [NotNull] TArgs args, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where TArgs : notnull
    {
        return AddDeclaration(recurringJobId, () => JobHandlers.CreateJob(args), cronExpression, options);
    }

    /// <summary>Declares a recurring run of a job class, identified by the name of the class.</summary>
    /// <typeparam name="TJob">The job class.</typeparam>
    /// <param name="cronExpression">The schedule, or a <c>%Section:Key%</c> placeholder.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add<TJob>([NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where TJob : class, IJob
    {
        return Add<TJob>(typeof(TJob).ToGenericTypeString(), cronExpression, options);
    }

    /// <summary>Declares a recurring run of a job class.</summary>
    /// <typeparam name="TJob">The job class.</typeparam>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="cronExpression">The schedule, or a <c>%Section:Key%</c> placeholder.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Add<TJob>([NotNull] string recurringJobId, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where TJob : class, IJob
    {
        return AddDeclaration(recurringJobId, JobHandlers.CreateJob<TJob>, cronExpression, options);
    }

    /// <summary>
    /// Declares a recurring job run by the only <see cref="IJobHandler{TArgs}"/> of <typeparamref name="TArgs"/>,
    /// identified by the name of the handler class.
    /// </summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The arguments every run gets.</param>
    /// <param name="cronExpression">The schedule, or a <c>%Section:Key%</c> placeholder.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet AddHandler<TArgs>([NotNull] TArgs args, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where TArgs : notnull
    {
        Job job = JobHandlers.CreateJob(args);
        return AddDeclaration(job.Type.ToGenericTypeString(), () => job, cronExpression, options);
    }

    /// <summary>Declares a recurring job run by <typeparamref name="THandler"/>, identified by the name of the class.</summary>
    /// <typeparam name="THandler">The handler to run.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The arguments every run gets.</param>
    /// <param name="cronExpression">The schedule, or a <c>%Section:Key%</c> placeholder.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet AddHandler<THandler, TArgs>([NotNull] TArgs args, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return AddHandler<THandler, TArgs>(typeof(THandler).ToGenericTypeString(), args, cronExpression, options);
    }

    /// <summary>Declares a recurring job run by <typeparamref name="THandler"/>.</summary>
    /// <typeparam name="THandler">The handler to run.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="recurringJobId">The recurring job's identifier.</param>
    /// <param name="args">The arguments every run gets.</param>
    /// <param name="cronExpression">The schedule, or a <c>%Section:Key%</c> placeholder.</param>
    /// <param name="options">The time zone, queue and other options; the defaults when omitted.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet AddHandler<THandler, TArgs>([NotNull] string recurringJobId, [NotNull] TArgs args, [NotNull] string cronExpression, [CanBeNull] RecurringJobOptions? options = null)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return AddDeclaration(recurringJobId, () => JobHandlers.CreateJob<THandler, TArgs>(args), cronExpression, options);
    }

    /// <summary>
    /// Replaces every <c>%Section:Key%</c> schedule with the value <paramref name="getValue"/> gives for
    /// <c>Section:Key</c>, and checks it. With <c>AddNightShiftRecurringJobs</c> the host's configuration is used
    /// without calling this. A set that still has a placeholder can't be applied.
    /// </summary>
    /// <param name="getValue">Looks up a value by its key, returning <c>null</c> when there is none.</param>
    /// <returns>This set, to chain calls.</returns>
    /// <exception cref="InvalidOperationException">A key has no value, or its value isn't a valid CRON expression.</exception>
    public RecurringJobSet ResolvePlaceholders([NotNull] Func<string, string?> getValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);

        for (int i = 0; i < _declarations.Count; i++)
        {
            Declaration declaration = _declarations[i];
            if (!IsPlaceholder(declaration.CronExpression)) continue;

            string key = declaration.CronExpression[1..^1];
            string? value = getValue(key);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"The recurring job '{declaration.Id}' takes its schedule from '{key}', which has no value.");
            }

            _declarations[i] = declaration with { CronExpression = Validated(declaration.Id, value) };
        }

        return this;
    }

    /// <summary>
    /// Also removes, after the declared jobs are applied, the stored recurring jobs that aren't declared. Off
    /// unless this is called, because it deletes jobs created any other way, and because during a rolling
    /// deployment the old and new versions each delete what only the other one declares. Pass
    /// <paramref name="scope"/> to limit it.
    /// </summary>
    /// <param name="scope">
    /// Tells which recurring job identifiers may be removed, for example the ones with a given prefix. Every
    /// identifier when omitted.
    /// </param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet RemoveUndeclared([CanBeNull] Func<string, bool>? scope = null)
    {
        _removeUndeclared = true;
        _removalScope = scope;
        return this;
    }

    /// <summary>Applies the set to <see cref="JobStorage.Current"/> through a new <see cref="RecurringJobManager"/>.</summary>
    /// <returns>What was applied and removed.</returns>
    public RecurringJobSetResult Apply()
    {
        return Apply(new RecurringJobManager(JobStorage.Current), JobStorage.Current);
    }

    /// <summary>
    /// Registers or updates every declared job and, after <see cref="RemoveUndeclared"/>, removes the undeclared
    /// ones. An error applying a job stops here and nothing is removed.
    /// </summary>
    /// <param name="manager">Registers and removes the jobs.</param>
    /// <param name="storage">The manager's storage, used to list the existing jobs.</param>
    /// <returns>What was applied and removed.</returns>
    public RecurringJobSetResult Apply([NotNull] IRecurringJobManager manager, [NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(storage);

        ThrowIfAnyPlaceholderRemains();

        foreach (Declaration declaration in _declarations)
        {
            manager.AddOrUpdate(declaration.Id, declaration.Job, declaration.CronExpression, declaration.Options);
        }

        return new RecurringJobSetResult([.. _declarations.Select(static declaration => declaration.Id)], RemoveUndeclaredJobs(manager, storage));
    }

    private RecurringJobSet AddDeclaration(string recurringJobId, Func<Job> createJob, string cronExpression, RecurringJobOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);
        ArgumentNullException.ThrowIfNull(cronExpression);

        if (_ids.Contains(recurringJobId))
        {
            throw new InvalidOperationException($"The recurring job '{recurringJobId}' is declared more than once.");
        }

        string schedule = IsPlaceholder(cronExpression) ? cronExpression : Validated(recurringJobId, cronExpression);

        _declarations.Add(new Declaration(recurringJobId, CreateJob(recurringJobId, createJob), schedule, options ?? new RecurringJobOptions()));
        _ids.Add(recurringJobId);
        return this;
    }

    private static bool IsPlaceholder(string cronExpression)
    {
        return cronExpression is ['%', _, .., '%'];
    }

    private void ThrowIfAnyPlaceholderRemains()
    {
        Declaration? unresolved = _declarations.Find(static declaration => IsPlaceholder(declaration.CronExpression));

        if (unresolved != null)
        {
            throw new InvalidOperationException($"The recurring job '{unresolved.Id}' has the unresolved schedule '{unresolved.CronExpression}'. Call {nameof(ResolvePlaceholders)} first.");
        }
    }

    private static Job CreateJob(string recurringJobId, Func<Job> createJob)
    {
        try
        {
            return createJob();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The recurring job '{recurringJobId}' can't be declared: {ex.Message}", ex);
        }
    }

    private static string Validated(string recurringJobId, string cronExpression)
    {
        try
        {
            RecurringJobEntity.ParseCronExpression(cronExpression);
            return cronExpression;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new InvalidOperationException($"The recurring job '{recurringJobId}' has an invalid CRON expression '{cronExpression}': {ex.Message}", ex);
        }
    }

    private List<string> RemoveUndeclaredJobs(IRecurringJobManager manager, JobStorage storage)
    {
        if (!_removeUndeclared) return [];

        if (_ids.Count == 0)
        {
            // An empty set is far more likely a configuration mistake than a wish to delete every schedule
            Logger.Warn("No recurring job was declared, so RemoveUndeclared left the stored recurring jobs alone.");
            return [];
        }

        using IStorageConnection connection = storage.GetConnection();

        List<string> removed = [.. connection.GetRecurringJobs()
            .Where(static job => !job.Removed)
            .Select(static job => job.Id)
            .Where(id => !_ids.Contains(id) && (_removalScope == null || _removalScope(id)))
            .OrderBy(static id => id, StringComparer.Ordinal)];

        removed.ForEach(manager.RemoveIfExists);
        return removed;
    }

    private sealed record Declaration(string Id, Job Job, string CronExpression, RecurringJobOptions Options);
}
