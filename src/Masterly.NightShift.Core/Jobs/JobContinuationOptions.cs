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

namespace Masterly.NightShift;

/// <summary>
/// Which outcomes of the parent start a continuation. The flags combine: <c>OnlyOnSucceededState | OnlyOnFailedState</c>
/// starts it when the parent succeeds or fails.
/// </summary>
[Flags]
public enum JobContinuationOptions
{
    /// <summary>When the parent succeeds or is deleted.</summary>
    OnAnyFinishedState = 0,

    /// <summary>When the parent succeeds.</summary>
    OnlyOnSucceededState = 1,

    /// <summary>When the parent is deleted.</summary>
    OnlyOnDeletedState = 2,

    /// <summary>
    /// When the parent fails for good, with no retry left. Failed isn't final: a continuation waiting for another
    /// outcome stays waiting, as the parent may still be requeued and succeed.
    /// </summary>
    OnlyOnFailedState = 4,

    /// <summary>
    /// When the parent starts processing, so the continuation runs alongside it. A continuation added after the parent
    /// started (it is processing, succeeded or failed) starts at once.
    /// </summary>
    OnlyOnProcessingState = 8,
}