// This file is part of Hangfire. Copyright © 2015 Hangfire OÜ.
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

// ReSharper disable InconsistentNaming

using System;
using System.ComponentModel;
using System.Threading;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// The format in which jobs, arguments, dates and state data are written to storage. Every version reads every
    /// format; the level only chooses what is written, so servers that know only older formats can still process
    /// the jobs. The default is <see cref="Version_110"/>, the oldest format, to keep a rolling upgrade safe. A new
    /// application should call <see cref="CompatibilityLevelExtensions.SetDataCompatibilityLevel"/> with
    /// <see cref="Version_180"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Public API, can not touch in minor versions.")]
    public enum CompatibilityLevel
    {
        /// <summary>The original format, readable by every older server. The default.</summary>
        Version_110 = 110,

        /// <summary>
        /// The compact job payload, millisecond timestamps, JSON dates in arguments, and internal data written
        /// with isolated options.
        /// </summary>
        Version_170 = 170,

        /// <summary>
        /// Everything in <see cref="Version_170"/>, without two writes that are no longer needed. The level a new
        /// application should use.
        /// </summary>
        Version_180 = 180,
    }

    /// <summary>
    /// The process-wide configuration of Masterly.NightShift: storage, activator, log provider, serializer, filters and
    /// dashboard extensions, set through the <c>Use*</c> extension methods on <see cref="Configuration"/>.
    /// </summary>
    public class GlobalConfiguration : IGlobalConfiguration
    {
        private static int _compatibilityLevel = (int)CompatibilityLevel.Version_110;

        public static IGlobalConfiguration Configuration { get; } = new GlobalConfiguration();

        internal static CompatibilityLevel CompatibilityLevel
        {
            get => (CompatibilityLevel)Volatile.Read(ref _compatibilityLevel);
            set => Volatile.Write(ref _compatibilityLevel, (int)value);
        }

        internal static bool HasCompatibilityLevel(CompatibilityLevel level)
        {
            return CompatibilityLevel >= level;
        }

        internal GlobalConfiguration()
        {
        }
    }

    /// <summary>
    /// Sets the <see cref="CompatibilityLevel"/> of the data written to storage.
    /// </summary>
    public static class CompatibilityLevelExtensions
    {
        /// <summary>
        /// Chooses the format in which data is written to storage, for every client, server and dashboard in this
        /// process. Without this call the level is <see cref="CompatibilityLevel.Version_110"/>, the oldest format,
        /// which keeps a rolling upgrade from older servers safe but isn't the better choice otherwise: a new
        /// application should use <see cref="CompatibilityLevel.Version_180"/>. When older servers share the
        /// storage, use the newest level that all of them read, and raise it once they are gone.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidEnumArgumentException"><paramref name="compatibilityLevel"/> isn't a defined value.</exception>
        public static IGlobalConfiguration SetDataCompatibilityLevel(
            [NotNull] this IGlobalConfiguration configuration,
            CompatibilityLevel compatibilityLevel)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            if (!Enum.IsDefined(typeof(CompatibilityLevel), compatibilityLevel))
                throw new InvalidEnumArgumentException(nameof(compatibilityLevel), (int) compatibilityLevel,
                    typeof(CompatibilityLevel));

            GlobalConfiguration.CompatibilityLevel = compatibilityLevel;

            return configuration;
        }
    }
}
