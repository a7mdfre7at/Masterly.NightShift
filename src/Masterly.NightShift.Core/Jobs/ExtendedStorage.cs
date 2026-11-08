#nullable enable

using System;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Reads through the optional members of <see cref="JobStorageConnection"/> (the <see cref="JobStorageFeatures.ExtendedApi"/>
    /// feature) for code that only has a connection. A storage that doesn't implement them throws
    /// <see cref="NotSupportedException"/>; the read then falls back to the members every storage has.
    /// </summary>
    internal static class ExtendedStorage
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ExtendedStorage));

        /// <summary>Reads with <paramref name="extended"/> when the storage has it, otherwise with <paramref name="fallback"/>.</summary>
        public static T Read<T>(
            [NotNull] IStorageConnection connection,
            [NotNull] Func<JobStorageConnection, T> extended,
            [NotNull] Func<IStorageConnection, T> fallback)
        {
            if (connection is JobStorageConnection storageConnection)
            {
                try
                {
                    return extended(storageConnection);
                }
                catch (NotSupportedException ex)
                {
                    Logger.DebugException("The storage doesn't support an extended read, reading through the basic API instead.", ex);
                }
            }

            return fallback(connection);
        }

        /// <summary>Whether the storage implements the optional members of <see cref="JobStorageConnection"/> and <see cref="JobStorageTransaction"/>.</summary>
        public static bool IsSupported([NotNull] JobStorage storage)
        {
            return storage.HasFeature(JobStorageFeatures.ExtendedApi);
        }
    }
}
