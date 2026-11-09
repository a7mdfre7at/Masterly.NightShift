// This file is part of Hangfire. Copyright © 2018 Hangfire OÜ.
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
/// The server a background process dispatcher is created for: its identifier, storage, properties and stop tokens.
/// </summary>
/// <param name="ServerId">The identifier of the server.</param>
/// <param name="Storage">The storage the server uses.</param>
/// <param name="Properties">The server's properties, shown on the dashboard.</param>
/// <param name="StoppingToken">Canceled as soon as the server is asked to stop.</param>
/// <param name="StoppedToken">Canceled when the stop timeout expires: the processes must stop now.</param>
/// <param name="ShutdownToken">Canceled when the shutdown timeout expires: running jobs are aborted.</param>
public sealed record BackgroundServerContext(
    [NotNull] string ServerId,
    [NotNull] JobStorage Storage,
    [NotNull] IDictionary<string, object> Properties,
    CancellationToken StoppingToken,
    CancellationToken StoppedToken,
    CancellationToken ShutdownToken)
{
    /// <summary>The identifier of the server.</summary>
    public string ServerId { get; } = ServerId ?? throw new ArgumentNullException(nameof(ServerId));

    /// <summary>The storage the server uses.</summary>
    public JobStorage Storage { get; } = Storage ?? throw new ArgumentNullException(nameof(Storage));

    /// <summary>The server's properties, shown on the dashboard.</summary>
    public IDictionary<string, object> Properties { get; } = Properties ?? throw new ArgumentNullException(nameof(Properties));
}
