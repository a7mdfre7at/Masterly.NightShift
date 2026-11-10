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

using Masterly.NightShift.Client;

namespace Masterly.NightShift;

/// <summary>
/// Provides methods for creating background jobs and changing their states.
/// Represents a default implementation of the <see cref="IBackgroundJobClient"/>
/// interface.
/// </summary>
/// 
/// <remarks>
/// <para>This class uses the <see cref="IBackgroundJobFactory"/> interface 
/// for creating background jobs and the <see cref="IBackgroundJobStateChanger"/> 
/// interface for changing their states. Please see documentation for those 
/// types and their implementations to learn the details.</para>
/// 
/// <note type="warning">
/// Despite the fact that instance methods of this class are thread-safe,
/// most implementations of the <see cref="IState"/> interface are <b>neither
/// thread-safe, nor immutable</b>. Please create a new instance of a state 
/// class for each operation to avoid race conditions and unexpected side 
/// effects.
/// </note>
/// </remarks>
/// 
/// <threadsafety static="true" instance="true" />
public class BackgroundJobClient : IBackgroundJobClient, IBackgroundJobClientV2
{
    private readonly IBackgroundJobFactory _factory;
    private readonly IBackgroundJobStateChanger _stateChanger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobClient"/>
    /// class with the storage from a global configuration.
    /// </summary>
    /// 
    /// <remarks>
    /// Please see the <see cref="GlobalConfiguration"/> class for the
    /// details regarding the global configuration.
    /// </remarks>
    public BackgroundJobClient()
        : this(JobStorage.Current)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobClient"/>
    /// class with the specified storage.
    /// </summary>
    /// 
    /// <param name="storage">Job storage to use for background jobs.</param>
    /// 
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public BackgroundJobClient([NotNull] JobStorage storage)
        : this(storage, JobFilterProviders.Providers)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobClient"/> class
    /// with the specified storage and filter provider.
    /// </summary>
    /// <param name="storage">Job storage to use for background jobs.</param>
    /// <param name="filterProvider">Filter provider responsible to locate job filters.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="filterProvider"/> is null.</exception>
    public BackgroundJobClient([NotNull] JobStorage storage, [NotNull] IJobFilterProvider filterProvider)
        : this(storage, new BackgroundJobFactory(filterProvider), new BackgroundJobStateChanger(filterProvider))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobClient"/> class
    /// with the specified storage, background job factory and state changer.
    /// </summary>
    /// 
    /// <param name="storage">Job storage to use for background jobs.</param>
    /// <param name="factory">Factory to create background jobs.</param>
    /// <param name="stateChanger">State changer to change states of background jobs.</param>
    /// 
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stateChanger"/> is null.</exception>
    public BackgroundJobClient(
        [NotNull] JobStorage storage,
        [NotNull] IBackgroundJobFactory factory,
        [NotNull] IBackgroundJobStateChanger stateChanger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(stateChanger);

        Storage = storage;
        _stateChanger = stateChanger;
        _factory = factory;
    }

    /// <inheritdoc />
    public JobStorage Storage { get; }

    public int RetryAttempts
    {
        get
        {
            if (_factory is BackgroundJobFactory factory)
            {
                return factory.RetryAttempts;
            }

            return 0;
        }
        set
        {
            if (_factory is BackgroundJobFactory factory)
            {
                factory.RetryAttempts = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets how many jobs <c>EnqueueMany</c> creates as one group. A group is created as a whole or not
    /// at all. The default is 250.
    /// </summary>
    public static int BulkSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 250;

    /// <summary>
    /// Creates the given jobs in groups of <see cref="BulkSize"/>, each job in the state the factory returns
    /// for it. Client filters run for every job.
    /// </summary>
    internal IReadOnlyList<string?> CreateMany(IReadOnlyList<Job> jobs, Func<IState> stateFactory)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(stateFactory);

        string?[] ids = new string?[jobs.Count];
        if (ids.Length == 0) return ids;

        try
        {
            using IStorageConnection connection = Storage.GetConnection();
            int bulkSize = BulkSize;

            for (int start = 0; start < ids.Length; start += bulkSize)
            {
                int count = Math.Min(bulkSize, ids.Length - start);
                CreateContext[] contexts = new CreateContext[count];

                for (int i = 0; i < count; i++)
                {
                    Job job = jobs[start + i] ?? throw new ArgumentException("The jobs can't contain null.", nameof(jobs));

                    // A state of its own for every job: filters change the state they are given (its queue, for example)
                    contexts[i] = new CreateContext(Storage, connection, job, stateFactory(), null);
                }

                if (_factory is BackgroundJobFactory factory)
                {
                    BackgroundJob?[] created = factory.CreateMany(contexts);
                    for (int i = 0; i < count; i++)
                    {
                        ids[start + i] = created[i]?.Id;
                    }
                }
                else
                {
                    // Another factory: one job at a time
                    for (int i = 0; i < count; i++)
                    {
                        ids[start + i] = _factory.Create(contexts[i])?.Id;
                    }
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new BackgroundJobClientException("Background job creation failed. See inner exception for details.", ex);
        }
    }

    /// <inheritdoc />
    public string? Create(Job job, IState state) => Create(job, state, null);

    /// <inheritdoc />
    public string? Create(Job job, IState state, IDictionary<string, object>? parameters)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            using IStorageConnection connection = Storage.GetConnection();
            CreateContext context = new(Storage, connection, job, state, parameters);
            BackgroundJob? backgroundJob = _factory.Create(context);

            return backgroundJob?.Id;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new BackgroundJobClientException("Background job creation failed. See inner exception for details.", ex);
        }
    }

    /// <inheritdoc />
    public bool ChangeState(string jobId, IState state, string? expectedState)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            using IStorageConnection connection = Storage.GetConnection();
            IState? appliedState = _stateChanger.ChangeState(new StateChangeContext(
                Storage,
                connection,
                jobId,
                state,
                expectedState != null ? [expectedState] : null));

            return appliedState != null && appliedState.Name.Equals(state.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new BackgroundJobClientException("State change of a background job failed. See inner exception for details", ex);
        }
    }
}
