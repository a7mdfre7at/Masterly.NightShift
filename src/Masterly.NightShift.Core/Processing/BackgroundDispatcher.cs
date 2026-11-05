// This file is part of Hangfire. Copyright © 2017 Hangfire OÜ.
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;
using ThreadState = System.Threading.ThreadState;

namespace Masterly.NightShift.Processing
{
    internal sealed class BackgroundDispatcher : IBackgroundDispatcher
    {
        private readonly ILog _logger = LogProvider.GetLogger(typeof(BackgroundDispatcher));
        private readonly CountdownEvent _stopped;

        private readonly IBackgroundExecution _execution;
        private readonly Action<Guid, object> _action;
        private readonly object _state;

        public BackgroundDispatcher(
            [NotNull] IBackgroundExecution execution,
            [NotNull] Action<Guid, object> action,
            [CanBeNull] object state,
            [NotNull] Func<ThreadStart, IEnumerable<Thread>> threadFactory)
        {
            if (threadFactory == null) throw new ArgumentNullException(nameof(threadFactory));

            _execution = execution ?? throw new ArgumentNullException(nameof(execution));
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _state = state;

            AppDomainUnloadMonitor.EnsureInitialized();

            var threads = threadFactory(DispatchLoop)?.ToArray();

            if (threads == null || threads.Length == 0)
            {
                throw new ArgumentException("At least one unstarted thread should be created.", nameof(threadFactory));
            }

            if (threads.Any(static thread => thread == null || (thread.ThreadState & ThreadState.Unstarted) == 0))
            {
                throw new ArgumentException("All the threads should be non-null and in the ThreadState.Unstarted state.", nameof(threadFactory));
            }

            _stopped = new CountdownEvent(threads.Length);

            foreach (var thread in threads)
            {
                thread.Start();
            }
        }

        public bool Wait(TimeSpan timeout)
        {
            return _stopped.WaitHandle.WaitOne(timeout);
        }

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _stopped.WaitHandle.WaitOneAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _execution.Dispose();
            _stopped.Dispose();
        }

        public override string ToString()
        {
            return _execution.ToString();
        }

        private void DispatchLoop()
        {
            try
            {
                _execution.Run(_action, _state);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                if (!(ex is ThreadAbortException) || !AppDomainUnloadMonitor.IsUnloading)
                {
                    try
                    {
                        _logger.FatalException("Dispatcher is stopped due to an exception, you need to restart the server manually. Please report it to Masterly.NightShift developers.", ex);
                    }
                    catch (Exception inner) when (inner.IsCatchableExceptionType())
                    {
                        Debug.WriteLine($"Dispatcher is stopped due to an exception, you need to restart the server manually. Please report it to Masterly.NightShift developers: {ex}");
                    }
                }
            }
            finally
            {
                try
                {
                    _stopped.Signal();
                }
                catch (ObjectDisposedException)
                {
                    Debug.WriteLine("Unable to signal the stopped event for BackgroundDispatcher: it was already disposed");
                }
            }
        }
    }
}