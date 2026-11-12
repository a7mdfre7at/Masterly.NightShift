namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// Storage mocks backed by dictionaries: hashes and sets that write transactions change when they commit, enough for
/// schedule policies and recurring jobs.
/// </summary>
internal sealed class PolicyStorage
{
    public PolicyStorage()
    {
        Storage.Setup(x => x.GetConnection()).Returns(() => Connection.Object);
        Storage.Setup(x => x.GetReadOnlyConnection()).Returns(() => Connection.Object);

        Connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>()))
            .Returns((string key) => Hashes.TryGetValue(key, out Dictionary<string, string> hash) ? new Dictionary<string, string>(hash) : null);
        Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>()))
            .Returns((string key) => Sets.TryGetValue(key, out SortedSet<string> set) ? [.. set] : []);
        Connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns((string resource, TimeSpan _) =>
            {
                Locks.Add(resource);
                return new Mock<IDisposable>().Object;
            });
        Connection.Setup(x => x.CreateWriteTransaction()).Returns(CreateTransaction);
    }

    public Mock<JobStorage> Storage { get; } = new();
    public Mock<IStorageConnection> Connection { get; } = new();
    public Dictionary<string, Dictionary<string, string>> Hashes { get; } = [];
    public Dictionary<string, SortedSet<string>> Sets { get; } = [];
    public List<string> Locks { get; } = [];
    public int Commits { get; private set; }

    /// <summary>Makes a transaction that removes a hash fail when it commits, after the hash is gone.</summary>
    public bool FailCommitsThatRemoveAHash { get; set; }

    public JobStorage Object => Storage.Object;

    /// <summary>Stores a policy as <c>SaveSchedulePolicy</c> would, with a revision.</summary>
    public void Add(SchedulePolicy policy, long revision = 1)
    {
        Dictionary<string, string> fields = policy.ToFields();
        fields[SchedulePolicy.RevisionField] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Hashes[SchedulePolicies.GetKey(policy.Name)] = fields;
        AddToSet(SchedulePolicies.SetKey, policy.Name);
    }

    /// <summary>Stores a recurring job hash and lists it.</summary>
    public void AddRecurringJob(string id, Dictionary<string, string> hash)
    {
        Hashes[$"recurring-job:{id}"] = hash;
        AddToSet("recurring-jobs", id);
    }

    private void AddToSet(string key, string value)
    {
        if (!Sets.TryGetValue(key, out SortedSet<string> set))
        {
            Sets[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        }

        set.Add(value);
    }

    private IWriteOnlyTransaction CreateTransaction()
    {
        List<Action> pending = [];
        Mock<IWriteOnlyTransaction> transaction = new();

        transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback((string key, IEnumerable<KeyValuePair<string, string>> fields) =>
            {
                List<KeyValuePair<string, string>> copy = [.. fields];
                pending.Add(() =>
                {
                    if (!Hashes.TryGetValue(key, out Dictionary<string, string> hash))
                    {
                        Hashes[key] = hash = [];
                    }

                    foreach (KeyValuePair<string, string> field in copy)
                    {
                        hash[field.Key] = field.Value;
                    }
                });
            });
        List<string> removedHashes = [];
        transaction.Setup(x => x.RemoveHash(It.IsAny<string>()))
            .Callback((string key) =>
            {
                removedHashes.Add(key);
                pending.Add(() => Hashes.Remove(key));
            });
        transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string key, string value) => pending.Add(() => AddToSet(key, value)));
        transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Callback((string key, string value, double _) => pending.Add(() => AddToSet(key, value)));
        transaction.Setup(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string key, string value) => pending.Add(() =>
            {
                if (Sets.TryGetValue(key, out SortedSet<string> set))
                {
                    set.Remove(value);
                }
            }));
        transaction.Setup(x => x.Commit()).Callback(() =>
        {
            if (FailCommitsThatRemoveAHash && removedHashes.Count > 0)
            {
                // The worst a storage without atomic transactions could do: the hash is gone, the rest isn't written
                foreach (string key in removedHashes)
                {
                    Hashes.Remove(key);
                }

                pending.Clear();
                throw new InvalidOperationException("The transaction failed after removing a hash.");
            }

            foreach (Action action in pending)
            {
                action();
            }

            pending.Clear();
            Commits++;
        });

        return transaction.Object;
    }
}
