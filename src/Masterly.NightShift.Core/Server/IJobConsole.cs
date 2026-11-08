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
    /// trimmed to <see cref="JobConsole.DefaultMaxLines"/> entries, and checkpoints also to the list
    /// <c>job:{id}:checkpoints</c>, which isn't trimmed. The lists live as long as the job's data does.
    /// The console can be used from several threads; calls run one at a time. Writes made after the
    /// performance ended, or after the job's cancellation token found the job aborted (deleted or requeued
    /// while it runs), are dropped. Writes are cheap but not free; report progress in steps, not for every item.
    /// When the storage fails, <see cref="SetProgress"/> and <see cref="WriteLine(JobConsoleLevel, string)"/> drop the
    /// write and log a warning instead of failing the job; <see cref="Checkpoint"/>, <see cref="Checkpoints"/> and
    /// <see cref="HasCheckpoint"/> throw, because a lost or misread checkpoint would make a retry redo finished steps.
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
        /// Requeuing a succeeded or deleted job clears them, so it runs from the start again; a failed job
        /// that is retried or requeued keeps them.
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
