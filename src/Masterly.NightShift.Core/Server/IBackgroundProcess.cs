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

namespace Masterly.NightShift.Server;

/// <summary>
/// A process that a <see cref="BackgroundProcessingServer"/> runs in a loop on a background thread for as long
/// as the server runs.
/// </summary>
/// <remarks>
/// Each call of <see cref="Execute"/> is one iteration: do one piece of work, or wait, then return. Wait through
/// the context (for example <see cref="BackgroundProcessContext.Wait"/>) so that the server can stop the process,
/// and dispose the storage connections you open within the iteration.
/// </remarks>
/// <seealso cref="BackgroundProcessingServer"/>
public interface IBackgroundProcess
{
    /// <summary>
    /// Runs one iteration of the process.
    /// </summary>
    /// <param name="context">Context for a background process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    void Execute([NotNull] BackgroundProcessContext context);
}