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
public class BackgroundServerContext(
    [NotNull] string serverId,
    [NotNull] JobStorage storage,
    [NotNull] IDictionary<string, object> properties,
    CancellationToken stoppingToken,
    CancellationToken stoppedToken,
    CancellationToken shutdownToken)
{
    public string ServerId { get; } = serverId ?? throw new ArgumentNullException(nameof(serverId));
    public JobStorage Storage { get; } = storage ?? throw new ArgumentNullException(nameof(storage));
    public IDictionary<string, object> Properties { get; } = properties ?? throw new ArgumentNullException(nameof(properties));
    public CancellationToken StoppingToken { get; } = stoppingToken;
    public CancellationToken StoppedToken { get; } = stoppedToken;
    public CancellationToken ShutdownToken { get; } = shutdownToken;
}
