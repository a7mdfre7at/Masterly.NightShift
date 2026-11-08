#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift
{
    /// <summary>
    /// Writes <see cref="ActivityFeed"/> entries to one storage, outside the state changes that produce them. Entries
    /// wait in memory and a single flush at a time writes them in batches, each batch in its own short transaction,
    /// so workers never wait for the <c>activity</c> list and a busy list can't block or fail a state change. The list
    /// is trimmed once every <see cref="TrimInterval"/> entries, not on every write.
    /// </summary>
    internal sealed class ActivityFeedWriter
    {
        /// <summary>How many entries may wait for the storage; more are dropped.</summary>
        internal const int MaxPending = 10_000;

        /// <summary>How many entries one transaction writes at most.</summary>
        internal const int BatchSize = 100;

        /// <summary>How many entries this process writes between two trims of the list.</summary>
        internal const int TrimInterval = 100;

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ActivityFeed));
        private static readonly ConditionalWeakTable<JobStorage, ActivityFeedWriter> Writers = new ConditionalWeakTable<JobStorage, ActivityFeedWriter>();

        private readonly JobStorage _storage;
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private int _pendingCount;
        private int _flushing;
        private int _sinceTrim = TrimInterval; // the first write of a process trims, in case MaxEntries was lowered
        private int _failing;
        private int _dropping;

        private ActivityFeedWriter(JobStorage storage)
        {
            _storage = storage;
        }

        /// <summary>Starts a flush. Replaceable for tests; runs it on the thread pool by default.</summary>
        internal static Action<Action> Schedule { get; set; } = static flush => ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), flush);

        public static ActivityFeedWriter For(JobStorage storage)
        {
            return Writers.GetValue(storage, static s => new ActivityFeedWriter(s));
        }

        /// <summary>Queues a serialized entry and makes sure a flush runs. Never throws.</summary>
        public void Add(string serialized)
        {
            if (Interlocked.Increment(ref _pendingCount) > MaxPending)
            {
                Interlocked.Decrement(ref _pendingCount);
                if (Interlocked.Exchange(ref _dropping, 1) == 0)
                {
                    Logger.Warn($"The activity feed is dropping entries: {MaxPending} are already waiting for the storage.");
                }

                return;
            }

            _pending.Enqueue(serialized);

            if (Interlocked.CompareExchange(ref _flushing, 1, 0) == 0)
            {
                try
                {
                    Schedule(Flush);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Volatile.Write(ref _flushing, 0);
                    Logger.WarnException("Could not start writing the activity feed.", ex);
                }
            }
        }

        /// <summary>Writes every queued entry, batch by batch. Runs on one thread at a time.</summary>
        internal void Flush()
        {
            while (true)
            {
                WriteBatch();

                if (!_pending.IsEmpty) continue;

                Volatile.Write(ref _flushing, 0);

                // An entry queued after the emptiness check saw _flushing == 1 and didn't start a flush: take it over,
                // unless another flush already did
                if (_pending.IsEmpty || Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return;
            }
        }

        private void WriteBatch()
        {
            var batch = new List<string>(BatchSize);
            while (batch.Count < BatchSize && _pending.TryDequeue(out var entry))
            {
                batch.Add(entry);
                Interlocked.Decrement(ref _pendingCount);
            }

            if (batch.Count == 0) return;

            _sinceTrim += batch.Count;
            var trim = _sinceTrim >= TrimInterval;

            try
            {
                using (var connection = _storage.GetConnection())
                using (var transaction = connection.CreateWriteTransaction())
                {
                    foreach (var entry in batch)
                    {
                        transaction.InsertToList(ActivityFeed.ListKey, entry);
                    }

                    if (trim)
                    {
                        transaction.TrimList(ActivityFeed.ListKey, 0, Math.Max(0, ActivityFeed.MaxEntries - 1));
                    }

                    transaction.Commit();
                }

                if (trim) _sinceTrim = 0;
                Volatile.Write(ref _failing, 0);
                Volatile.Write(ref _dropping, 0);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The state changes are committed already; only these entries are lost. One warning per outage.
                if (Interlocked.Exchange(ref _failing, 1) == 0)
                {
                    Logger.WarnException($"Could not write {batch.Count} activity feed entries, they are lost. Further failures are logged at the Debug level until a write succeeds.", ex);
                }
                else
                {
                    Logger.DebugException($"Could not write {batch.Count} activity feed entries, they are lost.", ex);
                }
            }
        }
    }
}
