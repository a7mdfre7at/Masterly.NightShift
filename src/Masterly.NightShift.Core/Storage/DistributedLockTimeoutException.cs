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

namespace Masterly.NightShift.Storage;

/// <summary>
/// Thrown when a distributed lock couldn't be acquired within its timeout.
/// </summary>
[Serializable]
public class DistributedLockTimeoutException(string resource) : TimeoutException(
        $"Timeout expired. The timeout elapsed prior to obtaining a distributed lock on the '{resource}' resource."
            )
{
    public string Resource { get; } = resource;
}
