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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift.Server
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
    /// Masterly.NightShift injects the console when the job runs.
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

        /// <summary>
        /// The checkpoints recorded by earlier attempts of this job (and this one so far). A retried job uses
        /// them to skip the steps that already finished, see <see cref="JobConsoleExtensions.Resume"/>.
        /// </summary>
        IReadOnlyList<string> Checkpoints { get; }

        /// <summary>Whether a checkpoint with this name was recorded, by this attempt or an earlier one.</summary>
        bool HasCheckpoint([NotNull] string name);
    }

    /// <summary>Resumable steps on top of <see cref="IJobConsole"/> checkpoints.</summary>
    public static class JobConsoleExtensions
    {
        /// <summary>
        /// Runs a step unless an earlier attempt already recorded its checkpoint, and records the checkpoint
        /// once it finishes. On a retry the job skips what it already did:
        /// <code>
        /// console.Resume("download", () => Download(file));
        /// console.Resume("parse", () => Parse(file));
        /// console.Resume("save", () => Save());
        /// </code>
        /// Steps must be safe to repeat from their start, since a crash in the middle of one runs it again.
        /// </summary>
        public static void Resume([NotNull] this IJobConsole console, [NotNull] string checkpoint, [NotNull] Action step)
        {
            if (console == null) throw new ArgumentNullException(nameof(console));
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            if (step == null) throw new ArgumentNullException(nameof(step));

            if (console.HasCheckpoint(checkpoint))
            {
                console.WriteLine($"Skipped '{checkpoint}': already done by an earlier attempt");
                return;
            }

            step();
            console.Checkpoint(checkpoint);
        }

        /// <summary>The asynchronous form of <see cref="Resume"/>.</summary>
        public static async Task ResumeAsync([NotNull] this IJobConsole console, [NotNull] string checkpoint, [NotNull] Func<Task> step)
        {
            if (console == null) throw new ArgumentNullException(nameof(console));
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            if (step == null) throw new ArgumentNullException(nameof(step));

            if (console.HasCheckpoint(checkpoint))
            {
                console.WriteLine($"Skipped '{checkpoint}': already done by an earlier attempt");
                return;
            }

            await step().ConfigureAwait(false);
            console.Checkpoint(checkpoint);
        }
    }
}
