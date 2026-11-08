#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.States
{
    /// <summary>
    /// Work that must wait until a state change is committed, such as raising an alert. The code that creates and
    /// commits the state change's transaction attaches it to the <see cref="ApplyStateContext"/> and runs it after
    /// the commit (and after releasing the job's lock, when it holds one). When applying or committing throws, it is
    /// dropped, so a state change that is retried doesn't do the work twice. Work added with
    /// <see cref="AddRegardless"/> runs in both cases.
    /// </summary>
    internal sealed class PostCommitActions
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(PostCommitActions));

        private readonly List<KeyValuePair<Action, bool>> _actions = new List<KeyValuePair<Action, bool>>();

        public void Add([NotNull] Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _actions.Add(new KeyValuePair<Action, bool>(action, false));
        }

        /// <summary>
        /// Queues work that also runs when applying or committing the state change throws: work about something
        /// already stored on its own, which a retried state change wouldn't do again.
        /// </summary>
        public void AddRegardless([NotNull] Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _actions.Add(new KeyValuePair<Action, bool>(action, true));
        }

        /// <summary>Runs every queued action. Called once the state change is committed.</summary>
        public void Run()
        {
            Run(onlyRegardless: false);
        }

        /// <summary>
        /// Runs only the actions queued with <see cref="AddRegardless"/> and drops the others. Called when applying
        /// or committing the state change threw.
        /// </summary>
        public void RunAfterFailure()
        {
            Run(onlyRegardless: true);
        }

        private void Run(bool onlyRegardless)
        {
            // The state change is committed already, or failed for another reason: a failing action must not make
            // the caller retry it, nor hide the original error
            foreach (var action in _actions)
            {
                if (onlyRegardless && !action.Value) continue;

                try
                {
                    action.Key();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.WarnException("An action that runs after a state change failed.", ex);
                }
            }

            _actions.Clear();
        }

        /// <summary>Queues the action when the state change has post-commit actions, otherwise runs it right away.</summary>
        public static void RunAfterCommit([CanBeNull] PostCommitActions? actions, [NotNull] Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (actions != null)
            {
                actions.Add(action);
            }
            else
            {
                action();
            }
        }

        /// <summary>
        /// Queues the action with <see cref="AddRegardless"/> when the state change has post-commit actions,
        /// otherwise runs it right away.
        /// </summary>
        public static void RunAfterCommitAttempt([CanBeNull] PostCommitActions? actions, [NotNull] Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (actions != null)
            {
                actions.AddRegardless(action);
            }
            else
            {
                action();
            }
        }
    }
}
