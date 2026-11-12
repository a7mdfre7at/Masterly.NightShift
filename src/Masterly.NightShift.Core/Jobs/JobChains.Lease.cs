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

    // How much later than a lease lasts its end may be read without the lease counting as damaged: clocks that
    // disagree, where the lease is written with the clock of a process
    private static readonly TimeSpan LeaseTolerance = TimeSpan.FromHours(1);

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
    internal readonly record struct ChainLeaseTime(DateTime Since, DateTime Until)
    {
        /// <summary>
        /// Whether the lease still lasts at <paramref name="now"/>. A lease is written to last
        /// <see cref="LeaseTime"/> from the moment it is written, so one that ends much later than that from now is
        /// damaged (or the clock it was written with is far off), and doesn't hold the steps of the chain for good:
        /// it counts as lapsed.
        /// </summary>
        public bool IsLive(DateTime now) => Until > now && Until - now <= LeaseTime + LeaseTolerance;
    }

    /// <summary>
    /// What a client holds while it creates, retries or replaces a chain: an entry in the chain's record, written
    /// before the first job is created or changed, that keeps every step of the chain from starting (see
    /// <see cref="JobChainStepFilter"/>). It lasts <see cref="LeaseTime"/> and is renewed while the client works,
    /// checked between two steps it creates; the final record is written without it. When the client stops in the
    /// middle, it lapses: the steps the record lists run again, and the jobs it doesn't list never do. A client that
    /// finds at a renewal, or before it writes the record, that its lease lapsed meanwhile (it was frozen, or one
    /// step took that long to create) checks that no server took the chain for abandoned before it goes on.
    /// </summary>
    private sealed class ChainLease
    {
        private readonly JobStorage _storage;
        private readonly IStorageConnection _connection;
        private readonly string _chainId;
        private readonly ChainRecord? _changed;
        private readonly DateTime _since;
        private DateTime _until;
        private long _renewedAt;

        private ChainLease(JobStorage storage, IStorageConnection connection, string chainId, ChainRecord? changed)
        {
            _storage = storage;
            _connection = connection;
            _chainId = chainId;
            _changed = changed;
            _since = StorageUtcNow(storage, connection);
        }

        /// <summary>
        /// Starts the record of a chain about to be created: only the lease, with no step. It isn't listed, so the
        /// chain doesn't show before it exists. It is kept as long as the record of the chain would be, counted
        /// from the time the first step is due: should the creation stop, a step that comes due finds a record
        /// that lists no step, and is deleted for it, however far ahead it was scheduled.
        /// </summary>
        public static ChainLease TakeForNewChain(JobStorage storage, IStorageConnection connection, string chainId, DateTime? firstStepDue)
        {
            ChainLease lease = new(storage, connection, chainId, null);
            DateTime now = JobClock.UtcNow;
            lease.Write(lease._since, RecordExpiry(now, firstStepDue ?? now) - now);
            return lease;
        }

        /// <summary>
        /// Takes the lease on a recorded chain, under the chain's lock: the steps the record lists stay listed, and
        /// none of them starts while it lasts.
        /// </summary>
        public static ChainLease Take(JobStorage storage, IStorageConnection connection, ChainRecord record)
        {
            ChainLease lease = new(storage, connection, record.ChainId, record);
            lease.Write(lease._since, expireIn: null);
            return lease;
        }

        /// <summary>
        /// Renews the lease when the client has worked for <see cref="LeaseRenewal"/> since it last did.
        /// </summary>
        /// <returns>True when the lease was renewed and had lapsed before: a server may have looked at the chain meanwhile.</returns>
        public bool RenewIfDue()
        {
            return Stopwatch.GetElapsedTime(_renewedAt) >= LeaseRenewal && Renew();
        }

        /// <summary>Renews the lease now.</summary>
        /// <returns>True when it had lapsed before, by the clock it is written with.</returns>
        public bool Renew()
        {
            DateTime now = StorageUtcNow(_storage, _connection);
            bool lapsed = now >= _until;
            Write(now, expireIn: null);
            return lapsed;
        }

        /// <summary>Whether the lease has lapsed, by the clock it is written with.</summary>
        public bool HasLapsed() => StorageUtcNow(_storage, _connection) >= _until;

        /// <summary>
        /// Gives the change up: the record goes back to what it was, without the lease. For a chain that was being
        /// created, the record started for it is removed when nothing of the chain is left
        /// (<paramref name="nothingLeft"/>), and kept without any step otherwise, so a job that couldn't be deleted
        /// finds a record that doesn't list it, and is deleted when it comes due. In both cases the chain isn't
        /// listed (any more), in the same write. Best effort: the lease lapses by itself.
        /// </summary>
        public void GiveUp(bool nothingLeft)
        {
            try
            {
                using IWriteOnlyTransaction transaction = _connection.CreateWriteTransaction();

                if (_changed != null)
                {
                    // Every field: the record of the change may have been written, by a write that threw
                    _changed.WriteFields(transaction);
                }
                else
                {
                    if (nothingLeft)
                    {
                        transaction.RemoveHash(GetKey(_chainId));
                    }
                    else
                    {
                        transaction.SetRangeInHash(GetKey(_chainId), new Dictionary<string, string> { [JobsField] = SerializeJobs(new Dictionary<string, string>(), null) });
                    }

                    transaction.RemoveFromSet(SetKey, _chainId);
                    transaction.RemoveFromSet(WatchSetKey, _chainId);
                }

                transaction.Commit();

                if (_changed == null)
                {
                    ForgetExpiry(_chainId);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException(
                    _changed != null
                        ? $"The change of chain '{_chainId}' was given up, but its record couldn't be told; its steps wait up to {LeaseTime.TotalSeconds:0} seconds more."
                        : $"The record started for chain '{_chainId}', which wasn't created, couldn't be removed. It lists no step, so nothing of the chain runs, and it expires by itself where the storage expires records.",
                    ex);
            }
        }

        private void Write(DateTime now, TimeSpan? expireIn)
        {
            // Before the write: the lease then counts from no later than the moment it is stored
            long started = Stopwatch.GetTimestamp();
            DateTime until = now + LeaseTime;

            using IWriteOnlyTransaction transaction = _connection.CreateWriteTransaction();
            transaction.SetRangeInHash(GetKey(_chainId), new Dictionary<string, string>
            {
                [JobsField] = SerializeJobs(_changed?.Jobs ?? new Dictionary<string, string>(), new ChainLeaseTime(_since, until)),
            });

            if (expireIn.HasValue && transaction is JobStorageTransaction storageTransaction && _storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                storageTransaction.ExpireHash(GetKey(_chainId), expireIn.Value);
            }

            transaction.Commit();
            _renewedAt = started;
            _until = until;
        }
    }
}
