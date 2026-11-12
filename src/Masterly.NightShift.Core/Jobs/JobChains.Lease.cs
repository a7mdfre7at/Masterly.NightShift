using System.Diagnostics;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

public static partial class JobChains
{
    /// <summary>
    /// The entry, in the record's map of jobs, that says the chain is being created or changed: the time it began
    /// and the time it lasts until unless it is renewed, <c>since/until</c>. No step key looks like it.
    /// </summary>
    internal const string LeaseKey = "changing";

    // How long the record of a chain being created is kept when its creation stops: long after its lease lapsed,
    // only so the record doesn't stay for good
    private static readonly TimeSpan PendingRecordLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a chain counts as being created or changed after its client last said so. The client says it again
    /// while it works, so this only bounds how long the steps of a chain wait after a client that stopped in the
    /// middle. Replaceable for tests.
    /// </summary>
    internal static TimeSpan LeaseTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a client works on a chain before it renews its lease. Replaceable for tests.</summary>
    internal static TimeSpan LeaseRenewal { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The time leases are written and read with: the storage's own clock where it has one, so the clocks of the
    /// client that changes a chain and of the server that checks it needn't agree; this process's clock otherwise.
    /// </summary>
    internal static DateTime StorageUtcNow(JobStorage storage, IStorageConnection connection)
    {
        return connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)
            ? storageConnection.GetUtcDateTime()
            : JobClock.UtcNow;
    }

    /// <summary>The map of jobs as the record keeps it, with the lease when the chain is being created or changed.</summary>
    private static string SerializeJobs(IReadOnlyDictionary<string, string> jobs, ChainLeaseTime? lease)
    {
        if (lease == null)
        {
            return SerializationHelper.Serialize(jobs, SerializationOption.Internal);
        }

        Dictionary<string, string> withLease = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> job in jobs)
        {
            withLease[job.Key] = job.Value;
        }

        withLease[LeaseKey] = $"{JobHelper.SerializeDateTime(lease.Value.Since)}/{JobHelper.SerializeDateTime(lease.Value.Until)}";
        return SerializationHelper.Serialize(withLease, SerializationOption.Internal);
    }

    /// <summary>Reads the map of jobs of a record, and the lease it carries when the chain is being created or changed.</summary>
    private static Dictionary<string, string> ParseJobs(string? serialized, out ChainLeaseTime? lease)
    {
        lease = null;

        Dictionary<string, string> jobs = string.IsNullOrEmpty(serialized)
            ? []
            : SerializationHelper.Deserialize<Dictionary<string, string>>(serialized, SerializationOption.Internal) ?? [];

        if (jobs.Remove(LeaseKey, out string? value))
        {
            int separator = value.IndexOf('/', StringComparison.Ordinal);
            if (separator > 0)
            {
                lease = new ChainLeaseTime(JobHelper.DeserializeDateTime(value[..separator]), JobHelper.DeserializeDateTime(value[(separator + 1)..]));
            }
        }

        return jobs;
    }

    /// <summary>When a chain began to be created or changed, and until when that counts unless it is renewed.</summary>
    internal readonly record struct ChainLeaseTime(DateTime Since, DateTime Until);

    /// <summary>
    /// What a client holds while it creates, retries or replaces a chain: an entry in the chain's record, written
    /// before the first job is created or changed, that keeps every step of the chain from starting (see
    /// <see cref="JobChainStepFilter"/>). It lasts <see cref="LeaseTime"/> and is renewed while the client works,
    /// checked between two steps it creates; the final record is written without it. When the client stops in the
    /// middle, it lapses: the steps the record lists run again, and the jobs it doesn't list never do.
    /// </summary>
    private sealed class ChainLease
    {
        private readonly JobStorage _storage;
        private readonly IStorageConnection _connection;
        private readonly string _chainId;
        private readonly IReadOnlyDictionary<string, string> _jobs;
        private readonly DateTime _since;
        private long _renewedAt;

        private ChainLease(JobStorage storage, IStorageConnection connection, string chainId, IReadOnlyDictionary<string, string> jobs)
        {
            _storage = storage;
            _connection = connection;
            _chainId = chainId;
            _jobs = jobs;
            _since = StorageUtcNow(storage, connection);
        }

        /// <summary>
        /// Starts the record of a chain about to be created: only the lease, with no step. It isn't listed, so the
        /// chain doesn't show before it exists, and it expires by itself should the creation stop.
        /// </summary>
        public static ChainLease TakeForNewChain(JobStorage storage, IStorageConnection connection, string chainId)
        {
            ChainLease lease = new(storage, connection, chainId, new Dictionary<string, string>(StringComparer.Ordinal));
            lease.Write(lease._since, expireIn: PendingRecordLifetime);
            return lease;
        }

        /// <summary>
        /// Takes the lease on a recorded chain, under the chain's lock: the steps the record lists stay listed, and
        /// none of them starts while it lasts.
        /// </summary>
        public static ChainLease Take(JobStorage storage, IStorageConnection connection, ChainRecord record)
        {
            ChainLease lease = new(storage, connection, record.ChainId, record.Jobs);
            lease.Write(lease._since, expireIn: null);
            return lease;
        }

        /// <summary>Renews the lease when the client has worked for <see cref="LeaseRenewal"/> since it last did.</summary>
        public void RenewIfDue()
        {
            if (Stopwatch.GetElapsedTime(_renewedAt) >= LeaseRenewal)
            {
                Write(StorageUtcNow(_storage, _connection), expireIn: null);
            }
        }

        /// <summary>
        /// Gives the lease back when a change is abandoned, so the chain's steps needn't wait for it to lapse.
        /// Best effort: it lapses by itself.
        /// </summary>
        public void Release()
        {
            try
            {
                using IWriteOnlyTransaction transaction = _connection.CreateWriteTransaction();
                transaction.SetRangeInHash(GetKey(_chainId), new Dictionary<string, string> { [JobsField] = SerializeJobs(_jobs, null) });
                transaction.Commit();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"The change of chain '{_chainId}' was abandoned, but its record couldn't be told; its steps wait up to {LeaseTime.TotalSeconds:0} seconds more.", ex);
            }
        }

        /// <summary>Removes the record of a chain that wasn't created after all. Best effort: it expires by itself.</summary>
        public void RemovePendingRecord()
        {
            try
            {
                using IWriteOnlyTransaction transaction = _connection.CreateWriteTransaction();
                transaction.RemoveHash(GetKey(_chainId));
                transaction.Commit();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"The record started for chain '{_chainId}', which wasn't created, couldn't be removed; it expires by itself where the storage expires records.", ex);
            }
        }

        private void Write(DateTime now, TimeSpan? expireIn)
        {
            // Before the write: the lease then counts from no later than the moment it is stored
            long started = Stopwatch.GetTimestamp();

            using IWriteOnlyTransaction transaction = _connection.CreateWriteTransaction();
            transaction.SetRangeInHash(GetKey(_chainId), new Dictionary<string, string>
            {
                [JobsField] = SerializeJobs(_jobs, new ChainLeaseTime(_since, now + LeaseTime)),
            });

            if (expireIn.HasValue && transaction is JobStorageTransaction storageTransaction && _storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                storageTransaction.ExpireHash(GetKey(_chainId), expireIn.Value);
            }

            transaction.Commit();
            _renewedAt = started;
        }
    }
}
