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

using System.Collections.ObjectModel;

namespace Masterly.NightShift.Client;

/// <summary>
/// Provides the context for the <see cref="IClientFilter.OnCreated"/> 
/// method of the <see cref="IClientFilter"/> interface.
/// </summary>
public class CreatedContext(
    [NotNull] CreateContext context,
    [CanBeNull] BackgroundJob? backgroundJob,
    bool canceled,
    [CanBeNull] Exception? exception) : CreateContext(context)
{
    [CanBeNull]
    public BackgroundJob? BackgroundJob { get; } = backgroundJob;

    public override IDictionary<string, object> Parameters => new ReadOnlyDictionary<string, object>(base.Parameters);

    /// <summary>
    /// Gets an exception that occurred during the creation of the job.
    /// </summary>
    [CanBeNull]
    public Exception? Exception { get; } = exception;

    /// <summary>
    /// Gets a value that indicates that this <see cref="CreatedContext"/>
    /// object was canceled.
    /// </summary>
    public bool Canceled { get; } = canceled;

    /// <summary>
    /// Gets or sets a value that indicates that this <see cref="CreatedContext"/>
    /// object handles an exception occurred during the creation of the job.
    /// </summary>
    public bool ExceptionHandled { get; set; }
}