// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Client;

internal sealed class CoreBackgroundJobFactory : IBackgroundJobFactory
{
    // Parameters that hold the library's own records, written with the internal options so the application's
    // serializer settings can't break them; their readers fall back to the user options for older data
    private static readonly FrozenSet<string> InternalParameters = new HashSet<string>(StringComparer.Ordinal)
    {
        JobTags.ParameterName, BatchJob.ParameterName, BatchJob.ContinuationParameterName,
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILog _logger = LogProvider.GetLogger(typeof(CoreBackgroundJobFactory));
    private readonly NightShiftLock _syncRoot = new();

    public CoreBackgroundJobFactory([NotNull] IStateMachine stateMachine)
    {
        StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        RetryAttempts = 0;
        RetryDelayFunc = GetRetryDelay;
    }

    public IStateMachine StateMachine { get; }

    public int RetryAttempts
    {
        get { lock (_syncRoot) { return field; } }
        set { lock (_syncRoot) { field = value; } }
    }

    public Func<int, TimeSpan> RetryDelayFunc
    {
        get { lock (_syncRoot) { return field; } }
        set { lock (_syncRoot) { field = value; } }
    }

    public BackgroundJob? Create(CreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Job.Queue != null && !context.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("Current storage doesn't support specifying queues directly for a specific job. Please use the QueueAttribute instead.");
        }

        Dictionary<string, string?> parameters = SerializeParameters(context);

        DateTime createdAt = JobClock.UtcNow;
        TimeSpan expireIn = TimeSpan.FromDays(30);

        return CreateBackgroundJobTwoSteps(context, parameters, createdAt, expireIn);
    }

    /// <summary>
    /// Creates the jobs of several contexts that share a storage and a connection: the job records in as few round
    /// trips as the storage allows, then every initial state in one transaction. Nothing is retried.
    /// </summary>
    internal BackgroundJob?[] CreateMany(IReadOnlyList<CreateContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        BackgroundJob?[] result = new BackgroundJob?[contexts.Count];
        if (result.Length == 0) return result;

        Job[] jobs = new Job[result.Length];
        Dictionary<string, string?>[] parameters = new Dictionary<string, string?>[result.Length];

        for (int i = 0; i < result.Length; i++)
        {
            CreateContext context = contexts[i];

            if (context.Job.Queue != null && !context.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
            {
                throw new NotSupportedException("Current storage doesn't support specifying queues directly for a specific job. Please use the QueueAttribute instead.");
            }

            jobs[i] = context.Job;
            parameters[i] = SerializeParameters(context);
        }

        IStorageConnection connection = contexts[0].Connection;
        DateTime createdAt = JobClock.UtcNow;
        TimeSpan expireIn = TimeSpan.FromDays(30);

        IReadOnlyList<string?> ids = connection is JobStorageConnection storageConnection
            ? storageConnection.CreateExpiredJobs(jobs, parameters, createdAt, expireIn)
            : CreateOneByOne(connection, jobs, parameters, createdAt, expireIn);

        // Work that waits for the commit, such as an activity feed entry, runs only once it succeeded
        PostCommitActions[] afterCommit = new PostCommitActions[result.Length];

        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();

            for (int i = 0; i < result.Length; i++)
            {
                afterCommit[i] = new PostCommitActions();

                string? jobId = ids[i];
                if (string.IsNullOrEmpty(jobId)) continue;

                CreateContext context = contexts[i];
                BackgroundJob backgroundJob = new(jobId, context.Job, createdAt, parameters[i]!); // A parameter set to null reads back as null
                result[i] = backgroundJob;

                if (context.InitialState == null) continue;

                ApplyStateContext applyContext = new(
                    context.Storage,
                    connection,
                    transaction,
                    backgroundJob,
                    context.InitialState,
                    oldStateName: null,
                    context.Profiler,
                    StateMachine)
                {
                    AfterCommit = afterCommit[i]
                };

                StateMachine.ApplyState(applyContext);
            }

            transaction.Commit();
        }
        catch
        {
            // Work about something already stored on its own still runs
            foreach (PostCommitActions? actions in afterCommit)
            {
                actions?.RunAfterFailure();
            }

            throw;
        }

        foreach (PostCommitActions actions in afterCommit)
        {
            actions.Run();
        }

        return result;
    }

    private static string?[] CreateOneByOne(
        IStorageConnection connection,
        Job[] jobs,
        Dictionary<string, string?>[] parameters,
        DateTime createdAt,
        TimeSpan expireIn)
    {
        string?[] ids = new string?[jobs.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = connection.CreateExpiredJob(jobs[i], parameters[i], createdAt, expireIn);
        }

        return ids;
    }

    private static Dictionary<string, string?> SerializeParameters(CreateContext context)
    {
        Dictionary<string, string?> parameters = context.Parameters.ToDictionary(
            static x => x.Key,
            static x => InternalParameters.Contains(x.Key)
                ? SerializationHelper.SerializeInternal(x.Value)
                : SerializationHelper.Serialize(x.Value, SerializationOption.User));

        // The moment a delayed job is meant to run, read by PerformContext.ScheduledFor. Retries schedule the job
        // again through the state machine, not here, so they keep the original moment.
        if (context.InitialState is ScheduledState scheduled && !parameters.ContainsKey(PerformContext.ScheduledForParameter))
        {
            parameters[PerformContext.ScheduledForParameter] =
                JobHelper.ToMillisecondTimestamp(scheduled.EnqueueAt).ToString(CultureInfo.InvariantCulture);
        }

        // The parents of a continuation, read by PerformContext.ParentId and ParentIds, written with the job
        if (context.InitialState is AwaitingState awaiting && !parameters.ContainsKey(PerformContext.ParentIdParameter))
        {
            parameters[PerformContext.ParentIdParameter] = SerializationHelper.Serialize(awaiting.ParentId);
        }
        else if (context.InitialState is AwaitingJobsState awaitingJobs && !parameters.ContainsKey(PerformContext.ParentIdsParameter))
        {
            parameters[PerformContext.ParentIdsParameter] = SerializationHelper.Serialize(awaitingJobs.ParentIds.ToArray());
        }

        return parameters;
    }

    private BackgroundJob? CreateBackgroundJobTwoSteps(CreateContext context, Dictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        int attemptsLeft = Math.Max(RetryAttempts, 0);

        // Retry may cause multiple background jobs to be created, especially when there's
        // a timeout-related exception. But initialization attempt will be performed only
        // for the most recent job, leaving all the previous ones in a non-initialized state
        // and making them invisible to other parts of the system, since no one knows their
        // identifiers. Since they also will be eventually expired leaving no trace, we can
        // consider that only one background job is created, regardless of retry attempts
        // number.
        string? jobId = RetryOnException(
            ref attemptsLeft,
            static (_, ctx) => ctx.Context.Connection.CreateExpiredJob(
                ctx.Context.Job,
                ctx.Parameters,
                ctx.CreatedAt,
                ctx.ExpireIn),
            new JobCreateContext { Context = context, Parameters = parameters, CreatedAt = createdAt, ExpireIn = expireIn });

        if (string.IsNullOrEmpty(jobId))
        {
            return null;
        }

        BackgroundJob backgroundJob = new(jobId, context.Job, createdAt, parameters!); // A parameter set to null reads back as null

        if (context.InitialState != null)
        {
            RetryOnException(ref attemptsLeft, static (attempt, ctx) =>
            {
                if (attempt > 0)
                {
                    // Normally, a distributed lock should be applied when making a retry, since
                    // it's possible to get a timeout exception, when transaction was actually
                    // committed. But since background job can't be returned to a position where
                    // its state is null, and since only the current thread knows the job's identifier
                    // when its state is null, and since we shouldn't do anything when it's non-null,
                    // there will be no any race conditions.
                    JobData data = ctx.Context.Connection.GetJobData(ctx.BackgroundJob.Id) ?? throw new InvalidOperationException($"Was unable to initialize a background job '{ctx.BackgroundJob.Id}', because it doesn't exist.");
                    if (!string.IsNullOrEmpty(data.State)) return;
                }

                // Work that waits for the commit, such as an activity feed entry, runs only once it succeeded
                PostCommitActions afterCommit = new();

                try
                {
                    using IWriteOnlyTransaction transaction = ctx.Context.Connection.CreateWriteTransaction();
                    ApplyStateContext applyContext = new(
                        ctx.Context.Storage,
                        ctx.Context.Connection,
                        transaction,
                        ctx.BackgroundJob,
                        ctx.Context.InitialState!, // Only reached when the job has an initial state
                        oldStateName: null,
                        ctx.Context.Profiler,
                        ctx.StateMachine)
                    {
                        AfterCommit = afterCommit
                    };

                    ctx.StateMachine.ApplyState(applyContext);

                    transaction.Commit();
                }
                catch
                {
                    // Work about something already stored on its own still runs, a retry wouldn't do it again
                    afterCommit.RunAfterFailure();
                    throw;
                }

                afterCommit.Run();
            }, new JobInitializeContext { Context = context, StateMachine = StateMachine, BackgroundJob = backgroundJob });
        }

        return backgroundJob;
    }

    private void RetryOnException<TContext>(ref int attemptsLeft, Action<int, TContext> action, TContext context)
    {
        RetryOnException(ref attemptsLeft, static (attempt, ctx) =>
        {
            ctx.Key(attempt, ctx.Value);
            return true;
        }, new KeyValuePair<Action<int, TContext>, TContext>(action, context));
    }

    private TResult RetryOnException<TContext, TResult>(ref int attemptsLeft, Func<int, TContext, TResult> action, TContext context)
    {
        List<Exception>? exceptions = null;
        int attempt = 0;
        TimeSpan delay = TimeSpan.Zero;

        do
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }

                return action(attempt++, context);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                (exceptions ??= []).Add(ex);
                _logger.DebugException("An exception occurred while creating a background job, see inner exception for details.", ex);
                delay = RetryDelayFunc(attempt);
            }
        } while (attemptsLeft-- > 0);

        if (exceptions is [var only])
        {
            ExceptionDispatchInfo.Capture(only).Throw();
        }

        throw new AggregateException(exceptions);
    }

    private static TimeSpan GetRetryDelay(int retryAttempt)
    {
        return retryAttempt switch
        {
            1 => TimeSpan.Zero,
            2 => TimeSpan.FromMilliseconds(50),
            3 => TimeSpan.FromMilliseconds(100),
            _ => TimeSpan.FromMilliseconds(500),
        };
    }

    private readonly record struct JobCreateContext
    {
        public required CreateContext Context { get; init; }
        public required Dictionary<string, string?> Parameters { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required TimeSpan ExpireIn { get; init; }
    }

    private readonly record struct JobInitializeContext
    {
        public required CreateContext Context { get; init; }
        public required IStateMachine StateMachine { get; init; }
        public required BackgroundJob BackgroundJob { get; init; }
    }
}