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

public partial class BackgroundJob
{
    /// <exclude />
    public BackgroundJob([NotNull] string id, [CanBeNull] Job? job, DateTime createdAt)
        : this(id, job, createdAt, null)
    {
    }

    /// <exclude />
    public BackgroundJob([NotNull] string id, [CanBeNull] Job? job, DateTime createdAt, [CanBeNull] IReadOnlyDictionary<string, string>? parametersSnapshot)
    {
        ArgumentNullException.ThrowIfNull(id);

        Id = id;
        Job = job;
        CreatedAt = createdAt;
        ParametersSnapshot = parametersSnapshot;
    }

    /// <exclude />
    [NotNull]
    public string Id { get; }

    /// <exclude />
    [CanBeNull]
    public Job? Job { get; }

    /// <exclude />
    public DateTime CreatedAt { get; }

    /// <exclude />
    [CanBeNull]
    public IReadOnlyDictionary<string, string>? ParametersSnapshot { get; }

    // When the performer called the job method, as a Stopwatch timestamp; 0 until then. It is here, and not in
    // PerformContext.Items, because every context of a performance shares this object and a field costs nothing.
    internal long MethodInvokedTimestamp
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}
