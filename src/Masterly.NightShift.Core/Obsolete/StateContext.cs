// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift.States
{
    /// <summary>
    /// The base class of the state contexts, holding the job.
    /// </summary>
    /// <exclude />
    [Obsolete("Use the BackgroundJob property of the state context instead. Kept for code ported from Hangfire.")]
    public abstract class StateContext
    {
        [NotNull]
        public abstract BackgroundJob BackgroundJob { get; }

        [NotNull]
        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public string JobId => BackgroundJob.Id;

        [CanBeNull]
        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public Job Job => BackgroundJob.Job;

        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public DateTime CreatedAt => BackgroundJob.CreatedAt;
    }
}