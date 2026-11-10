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

using System.Collections;

namespace Masterly.NightShift.SqlServer;

/// <summary>
/// The queue implementations of a <see cref="SqlServerStorage"/>: a default one, and others for particular queues.
/// </summary>
public class PersistentJobQueueProviderCollection : IEnumerable<IPersistentJobQueueProvider>
{
    private readonly List<IPersistentJobQueueProvider> _providers
        = [];
    private readonly Dictionary<string, IPersistentJobQueueProvider> _providersByQueue
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly IPersistentJobQueueProvider _defaultProvider;

    /// <summary>
    /// Initializes the collection with the provider used for queues that have none of their own.
    /// </summary>
    /// <param name="defaultProvider">The default provider.</param>
    public PersistentJobQueueProviderCollection(IPersistentJobQueueProvider defaultProvider)
    {
        ArgumentNullException.ThrowIfNull(defaultProvider);

        _defaultProvider = defaultProvider;

        _providers.Add(_defaultProvider);
    }

    /// <summary>
    /// Uses <paramref name="provider"/> for the given queues.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="queues">The queues it serves.</param>
    /// <exception cref="ArgumentException">A queue already has a provider.</exception>
    public void Add(IPersistentJobQueueProvider provider, IEnumerable<string> queues)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(queues);

        _providers.Add(provider);

        foreach (string queue in queues)
        {
            _providersByQueue.Add(queue, provider);
        }
    }

    /// <summary>
    /// Gets the provider of a queue, or the default provider when the queue has none of its own.
    /// </summary>
    /// <param name="queue">The queue name.</param>
    public IPersistentJobQueueProvider GetProvider(string queue)
    {
        return _providersByQueue.TryGetValue(queue, out IPersistentJobQueueProvider? provider)
            ? provider
            : _defaultProvider;
    }

    /// <summary>
    /// Enumerates the providers, the default one first.
    /// </summary>
    public IEnumerator<IPersistentJobQueueProvider> GetEnumerator()
    {
        return _providers.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}