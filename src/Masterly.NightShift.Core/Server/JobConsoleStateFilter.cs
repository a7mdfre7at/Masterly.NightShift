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
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Keeps a job's console list alive exactly as long as the job: it expires with the job when the job
    /// reaches a final state and is persisted again when the job is re-queued. Registered globally by default.
    /// Storages whose transactions don't derive from <see cref="JobStorageTransaction"/> keep the list forever;
    /// nothing else breaks.
    /// </summary>
    public sealed class JobConsoleStateFilter : JobFilterAttribute, IApplyStateFilter
    {
        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            if (transaction is not JobStorageTransaction storageTransaction)
            {
                return;
            }

            var key = JobConsole.GetConsoleKey(context.BackgroundJob.Id);

            if (context.NewState.IsFinal)
            {
                storageTransaction.ExpireList(key, context.JobExpirationTimeout);
            }
            else
            {
                storageTransaction.PersistList(key);
            }
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
