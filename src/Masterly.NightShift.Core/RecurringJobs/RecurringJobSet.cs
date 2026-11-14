using System.Linq.Expressions;
using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;

namespace Masterly.NightShift;

/// <summary>What <see cref="RecurringJobSet.Apply(IRecurringJobManager, JobStorage)"/> did.</summary>
/// <param name="Applied">The identifiers of the recurring jobs registered or updated.</param>
/// <param name="Removed">The identifiers of the undeclared recurring jobs removed.</param>
/// <param name="Retired">The identifiers of the undeclared recurring jobs paused by <see cref="RecurringJobSet.RetireUndeclared"/>.</param>
/// <param name="CreatedOnce">The keys of the jobs <c>Once</c> declared that this call created.</param>
/// <param name="PoliciesSaved">The names of the schedule policies <see cref="RecurringJobSet.AddPolicy"/> declared that this call created or changed.</param>
public sealed record RecurringJobSetResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Retired,
    IReadOnlyList<string> CreatedOnce,
    IReadOnlyList<string> PoliciesSaved);

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

    /// <summary>The recurring job hash field that marks a job registered by a set.</summary>
    internal const string DeclaredField = "Declared";

    /// <summary>The recurring job hash field that marks a declared job paused because it is no longer declared.</summary>
    internal const string RetiredField = "Retired";

    /// <summary>The storage hash that maps the key of each job <c>Once</c> declared to the id of the job it created.</summary>
    public const string OnceHashKey = "recurring-job-set:once";

    // SQL Server keeps hash fields in 100 characters
    private const int MaxOnceKeyLength = 100;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);

    private readonly List<Declaration> _declarations = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly List<OnceDeclaration> _once = [];
    private readonly HashSet<string> _onceKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchedulePolicy> _policies = new(StringComparer.Ordinal);
    private bool _removeUndeclared;
    private bool _retireUndeclared;
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
            if (!IsPlaceholder(declaration.CronExpression))
            {
                continue;
            }

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
        if (_retireUndeclared)
        {
            throw new InvalidOperationException($"{nameof(RetireUndeclared)} was already called; use one or the other.");
        }

        _removeUndeclared = true;
        _removalScope = scope;
        return this;
    }

    /// <summary>
    /// Also pauses, after the declared jobs are applied, the stored recurring jobs that a set declared before but
    /// this one doesn't, and marks them as retired. A retired job keeps its definition and history, and declaring
    /// it again resumes it. Jobs created any other way (from code with <c>AddOrUpdate</c>, from the dashboard or a
    /// manifest) are never touched. Pass <paramref name="scope"/> to limit it further.
    /// </summary>
    /// <param name="scope">
    /// Tells which recurring job identifiers may be retired, for example the ones with a given prefix. Every
    /// identifier when omitted. A stored job records only that a set declared it, not which one: when several
    /// applications declare sets in one storage, give each a scope, or each start of one pauses the jobs of the
    /// others. A set without a scope that retires jobs logs a warning.
    /// </param>
    /// <returns>This set, to chain calls.</returns>
    /// <exception cref="InvalidOperationException"><see cref="RemoveUndeclared"/> was called.</exception>
    /// <remarks>Pausing needs a manager that implements <see cref="IRecurringJobManagerV3"/>, as <see cref="RecurringJobManager"/> does.</remarks>
    public RecurringJobSet RetireUndeclared([CanBeNull] Func<string, bool>? scope = null)
    {
        if (_removeUndeclared)
        {
            throw new InvalidOperationException($"{nameof(RemoveUndeclared)} was already called; use one or the other.");
        }

        _retireUndeclared = true;
        _removalScope = scope;
        return this;
    }

    /// <summary>
    /// Declares a job created once for <paramref name="key"/>, the first time a set with it is applied: start-up
    /// work that must not run again on each start or on each replica, such as seeding data or a one-time
    /// migration. The key is recorded in the storage hash <see cref="OnceHashKey"/>, so later applications skip
    /// it; to run the work again, use another key.
    /// </summary>
    /// <param name="key">The key, up to 100 characters.</param>
    /// <param name="methodCall">The method to call.</param>
    /// <param name="delay">How long after the application to run it, or <c>null</c> to enqueue it at once.</param>
    /// <returns>This set, to chain calls.</returns>
    public RecurringJobSet Once([NotNull] string key, [NotNull, InstantHandle] Expression<Action> methodCall, TimeSpan? delay = null)
    {
        return AddOnce(key, () => Job.FromExpression(methodCall), delay);
    }

    /// <inheritdoc cref="Once(string, Expression{Action}, TimeSpan?)"/>
    public RecurringJobSet Once([NotNull] string key, [NotNull, InstantHandle] Expression<Func<Task>> methodCall, TimeSpan? delay = null)
    {
        return AddOnce(key, () => Job.FromExpression(methodCall), delay);
    }

    /// <inheritdoc cref="Once(string, Expression{Action}, TimeSpan?)"/>
    public RecurringJobSet Once<T>([NotNull] string key, [NotNull, InstantHandle] Expression<Action<T>> methodCall, TimeSpan? delay = null)
    {
        return AddOnce(key, () => Job.FromExpression(methodCall), delay);
    }

    /// <inheritdoc cref="Once(string, Expression{Action}, TimeSpan?)"/>
    public RecurringJobSet Once<T>([NotNull] string key, [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, TimeSpan? delay = null)
    {
        return AddOnce(key, () => Job.FromExpression(methodCall), delay);
    }

    /// <summary>
    /// Declares a schedule policy, saved before the jobs when the set is applied, so the jobs can follow it. An
    /// unchanged policy keeps its revision; a changed one replaces the stored one, whoever edited it, and reschedules
    /// the recurring jobs that follow it. A policy no longer declared stays stored.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>This set, to chain calls.</returns>
    /// <exception cref="InvalidOperationException">A policy of the same name is already declared.</exception>
    public RecurringJobSet AddPolicy([NotNull] SchedulePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!_policies.TryAdd(policy.Name, policy))
        {
            throw new InvalidOperationException($"Schedule policy '{policy.Name}' is declared more than once.");
        }

        return this;
    }

    /// <summary>The names of the declared schedule policies.</summary>
    public IReadOnlyCollection<string> DeclaredPolicies => _policies.Keys;

    /// <summary>Applies the set to <see cref="JobStorage.Current"/> through a new <see cref="RecurringJobManager"/>.</summary>
    /// <returns>What was applied and removed.</returns>
    public RecurringJobSetResult Apply()
    {
        return Apply(new RecurringJobManager(JobStorage.Current), JobStorage.Current);
    }

    /// <summary>
    /// Saves the declared schedule policies, registers or updates every declared job and, after
    /// <see cref="RemoveUndeclared"/>, removes the undeclared ones. The policies and the policies the jobs follow are
    /// checked before anything is written. An error applying a job stops here and nothing is removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A declared policy or job refers to a policy that is neither declared nor stored, or the policies form a cycle.
    /// </exception>
    /// <param name="manager">Registers and removes the jobs.</param>
    /// <param name="storage">The manager's storage, used to list the existing jobs.</param>
    /// <returns>What was applied and removed.</returns>
    public RecurringJobSetResult Apply([NotNull] IRecurringJobManager manager, [NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(storage);

        ThrowIfAnyPlaceholderRemains();

        List<SchedulePolicy> policies = OrderPolicies(storage);
        List<string> policiesSaved = [];
        foreach (SchedulePolicy policy in policies)
        {
            SchedulePolicies.Write(storage, policy, expectedRevision: null, out bool written);
            if (written)
            {
                policiesSaved.Add(policy.Name);
            }
        }

        foreach (Declaration declaration in _declarations)
        {
            manager.AddOrUpdate(declaration.Id, declaration.Job, declaration.CronExpression, declaration.Options);
            MarkDeclared(manager, storage, declaration.Id);
        }

        return new RecurringJobSetResult(
            [.. _declarations.Select(static declaration => declaration.Id)],
            RemoveUndeclaredJobs(manager, storage),
            RetireUndeclaredJobs(manager, storage),
            CreateOnceJobs(storage),
            policiesSaved);
    }

    // Checks the declared policies and the policies the jobs follow before anything is written, and orders the
    // declared policies so each one comes after the policies it refers to
    private List<SchedulePolicy> OrderPolicies(JobStorage storage)
    {
        IEnumerable<string> jobPolicies = _declarations
            .Select(static x => x.Options.Policy)
            .OfType<string>();
        if (_policies.Count == 0 && !jobPolicies.Any())
        {
            return [];
        }

        using IStorageConnection connection = storage.GetConnection();

        foreach (string name in _policies.Values.SelectMany(static x => x.Required.Concat(x.Excepted)).Concat(jobPolicies).Distinct(StringComparer.Ordinal))
        {
            if (!_policies.ContainsKey(name) && !SchedulePolicies.Exists(connection, name))
            {
                throw new InvalidOperationException($"Schedule policy '{name}' is used but neither declared nor stored.");
            }
        }

        List<SchedulePolicy> ordered = [];
        Dictionary<string, bool> visited = new(StringComparer.Ordinal);

        foreach (SchedulePolicy policy in _policies.Values.OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            Visit(policy, []);
        }

        return ordered;

        void Visit(SchedulePolicy policy, List<string> path)
        {
            if (visited.TryGetValue(policy.Name, out bool done))
            {
                if (done)
                {
                    return;
                }

                throw new InvalidOperationException($"Schedule policies form a cycle: {string.Join(" -> ", path.Append(policy.Name))}.");
            }

            visited[policy.Name] = false;
            foreach (string reference in policy.Required.Concat(policy.Excepted))
            {
                if (_policies.TryGetValue(reference, out SchedulePolicy? referenced))
                {
                    Visit(referenced, [.. path, policy.Name]);
                }
            }

            visited[policy.Name] = true;
            ordered.Add(policy);
        }
    }

    // Marks the job as declared, and resumes it when it was retired. Writes only what changed, so applying an
    // unchanged set at every start writes nothing.
    private static void MarkDeclared(IRecurringJobManager manager, JobStorage storage, string recurringJobId)
    {
        Dictionary<string, string>? hash;
        using (IStorageConnection connection = storage.GetConnection())
        {
            hash = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
        }

        if (hash == null)
        {
            return;
        }

        bool declared = hash.TryGetValue(DeclaredField, out string? declaredValue) && declaredValue == "1";
        bool retired = hash.TryGetValue(RetiredField, out string? retiredValue) && retiredValue == "1";

        if (declared && !retired)
        {
            return;
        }

        Dictionary<string, string> fields = new()
        {
            [DeclaredField] = "1",
            [RetiredField] = string.Empty,
        };

        // Resumed and no longer retired in one transaction: a job resumed but still marked retired would never be
        // retired again
        if (retired && manager is RecurringJobManager recurringJobManager)
        {
            recurringJobManager.SetPaused(recurringJobId, paused: false, fields);
            Logger.Info($"Recurring job '{recurringJobId}' is declared again and was resumed.");
            return;
        }

        if (retired && manager is IRecurringJobManagerV3 managerV3)
        {
            managerV3.Resume(recurringJobId);
            Logger.Info($"Recurring job '{recurringJobId}' is declared again and was resumed.");
        }

        SetFields(storage, recurringJobId, fields);
    }

    private List<string> RetireUndeclaredJobs(IRecurringJobManager manager, JobStorage storage)
    {
        if (!_retireUndeclared)
        {
            return [];
        }

        if (_ids.Count == 0)
        {
            Logger.Warn("No recurring job was declared, so RetireUndeclared left the stored recurring jobs alone.");
            return [];
        }

        if (manager is not IRecurringJobManagerV3 managerV3)
        {
            Logger.Warn($"RetireUndeclared needs a manager that implements {nameof(IRecurringJobManagerV3)}, so the undeclared recurring jobs were left alone.");
            return [];
        }

        List<string> retired;
        using (IStorageConnection connection = storage.GetConnection())
        {
            retired = [.. connection.GetRecurringJobs()
                .Where(static job => !job.Removed && job.Declared && !job.Retired)
                .Select(static job => job.Id)
                .Where(id => !_ids.Contains(id) && (_removalScope == null || _removalScope(id)))
                .OrderBy(static id => id, StringComparer.Ordinal)];
        }

        foreach (string id in retired)
        {
            Dictionary<string, string> fields = new() { [RetiredField] = "1" };

            // Paused and marked retired in one transaction: a job paused but not marked would stay paused when a
            // set declares it again
            if (managerV3 is RecurringJobManager recurringJobManager)
            {
                recurringJobManager.SetPaused(id, paused: true, fields);
            }
            else
            {
                managerV3.Pause(id);
                SetFields(storage, id, fields);
            }

            Logger.Info($"Recurring job '{id}' is no longer declared and was paused.");
        }

        // Nothing tells whose set declared a job: without a scope, the jobs of another application's set in the
        // same storage look like jobs this one stopped declaring
        if (retired.Count > 0 && _removalScope == null)
        {
            Logger.Warn($"RetireUndeclared paused {retired.Count} recurring job(s) that a set declared before and this one doesn't: {string.Join(", ", retired)}. When several applications declare sets in one storage, give each set a scope, or each start of one pauses the jobs of the others.");
        }

        return retired;
    }

    private static void SetFields(JobStorage storage, string recurringJobId, Dictionary<string, string> fields)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

        transaction.SetRangeInHash($"recurring-job:{recurringJobId}", fields);
        transaction.Commit();
    }

    // Under a lock, so replicas starting together create each job once. The job is created without a state, and
    // started in the transaction that records its key, as the recurring job scheduler does: a failure in between
    // leaves a job that never runs, and the next application creates it again
    private List<string> CreateOnceJobs(JobStorage storage)
    {
        if (_once.Count == 0)
        {
            return [];
        }

        List<string> created = [];
        BackgroundJobFactory factory = new(JobFilterProviders.Providers);

        using IStorageConnection connection = storage.GetConnection();
        using IDisposable distributedLock = connection.AcquireDistributedLock($"{OnceHashKey}:lock", LockTimeout);

        Dictionary<string, string> done = connection.GetAllEntriesFromHash(OnceHashKey) ?? [];

        foreach (OnceDeclaration once in _once)
        {
            if (done.ContainsKey(once.Key))
            {
                continue;
            }

            BackgroundJob? backgroundJob = factory.Create(new CreateContext(storage, connection, once.Job, null));

            if (string.IsNullOrEmpty(backgroundJob?.Id))
            {
                Logger.Warn($"The once job '{once.Key}' was not created, because a client filter canceled it. The next application tries again.");
                continue;
            }

            Start(storage, connection, factory.StateMachine, backgroundJob, once);
            created.Add(once.Key);
        }

        return created;
    }

    private static void Start(JobStorage storage, IStorageConnection connection, IStateMachine stateMachine, BackgroundJob backgroundJob, OnceDeclaration once)
    {
        IState state = once.Delay.HasValue && once.Delay.Value > TimeSpan.Zero
            ? new ScheduledState(once.Delay.Value)
            : new EnqueuedState();

        PostCommitActions afterCommit = new();

        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            stateMachine.ApplyState(new ApplyStateContext(storage, connection, transaction, backgroundJob, state, null, EmptyProfiler.Instance, stateMachine)
            {
                AfterCommit = afterCommit,
            });
            transaction.SetRangeInHash(OnceHashKey, [new KeyValuePair<string, string>(once.Key, backgroundJob.Id)]);
            transaction.Commit();
        }
        catch
        {
            // Work about something already stored on its own still runs, a retry wouldn't do it again
            afterCommit.RunAfterFailure();
            throw;
        }

        afterCommit.Run();
    }

    private RecurringJobSet AddOnce(string key, Func<Job> createJob, TimeSpan? delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Length > MaxOnceKeyLength)
        {
            throw new ArgumentException($"The key of a once job can be at most {MaxOnceKeyLength} characters long.", nameof(key));
        }

        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "The delay can't be negative.");
        }

        if (!_onceKeys.Add(key))
        {
            throw new InvalidOperationException($"The once job '{key}' is declared more than once.");
        }

        _once.Add(new OnceDeclaration(key, CreateJob(key, createJob), delay));
        return this;
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
        if (!_removeUndeclared)
        {
            return [];
        }

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

    private sealed record OnceDeclaration(string Key, Job Job, TimeSpan? Delay);
}
