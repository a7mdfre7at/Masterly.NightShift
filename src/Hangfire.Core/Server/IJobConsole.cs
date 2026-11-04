// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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

#nullable enable

using Hangfire.Annotations;

namespace Hangfire.Server
{
    /// <summary>Severity of a job console line.</summary>
    public enum JobConsoleLevel
    {
        /// <summary>Diagnostic detail.</summary>
        Debug,

        /// <summary>Normal progress information.</summary>
        Info,

        /// <summary>Something to look at, the job carries on.</summary>
        Warning,

        /// <summary>A failure the job reports itself.</summary>
        Error,
    }

    /// <summary>
    /// Lets a running job report what it is doing, live, to the dashboard: a progress percentage, console
    /// lines and named checkpoints with timestamps. Get it from <see cref="PerformContext.Console"/>, or
    /// declare a parameter of this type on the job method and pass <c>null</c> when creating the job:
    /// Hangfire injects the console when the job runs.
    /// </summary>
    /// <remarks>
    /// Everything is written to the job's storage as it happens: progress to the <c>Progress</c> job
    /// parameter (one value, overwritten), lines and checkpoints to the list <c>job:{id}:console</c>,
    /// trimmed to <see cref="JobConsole.DefaultMaxLines"/> entries. The list lives as long as the job's
    /// data does. Writes are cheap but not free; report progress in steps, not for every item.
    /// </remarks>
    public interface IJobConsole
    {
        /// <summary>Reports how far the job got, from 0 to 100, with an optional short description of the current step.</summary>
        void SetProgress(double percent, [CanBeNull] string? message = null);

        /// <summary>Writes an informational line.</summary>
        void WriteLine([NotNull] string message);

        /// <summary>Writes a line with the given severity.</summary>
        void WriteLine(JobConsoleLevel level, [NotNull] string message);

        /// <summary>
        /// Records that a named step was reached, so the dashboard can show the job's timeline and how long
        /// each step took.
        /// </summary>
        void Checkpoint([NotNull] string name);
    }
}
