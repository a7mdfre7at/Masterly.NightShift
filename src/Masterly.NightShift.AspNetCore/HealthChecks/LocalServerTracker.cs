#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.HealthChecks
{
    /// <summary>
    /// Remembers which servers run in this process, for the <see cref="NightShiftHealthCheckScope.Process"/> health
    /// check. Registered as an <see cref="IBackgroundProcess"/>, so every server that <c>AddNightShiftServer</c>
    /// starts runs it: it records the server's id, waits until the server stops, and forgets the id.
    /// </summary>
    internal sealed class LocalServerTracker : IBackgroundProcess
    {
        private static readonly ConcurrentDictionary<string, JobStorage> Servers = new ConcurrentDictionary<string, JobStorage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The ids of the servers of this process that use <paramref name="storage"/>.</summary>
        public static IReadOnlyList<string> GetServerIds(JobStorage storage)
        {
            return Servers.Where(pair => ReferenceEquals(pair.Value, storage)).Select(static pair => pair.Key).ToList();
        }

        public void Execute(BackgroundProcessContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            Servers[context.ServerId] = context.Storage;
            try
            {
                // Holds this process's thread until the server stops; the server never calls it again after that
                context.StoppingToken.WaitHandle.WaitOne();
            }
            finally
            {
                Servers.TryRemove(context.ServerId, out _);
            }
        }

        public override string ToString() => "NightShift health check";
    }
}
