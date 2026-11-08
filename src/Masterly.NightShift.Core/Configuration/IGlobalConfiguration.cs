// This file is part of Hangfire. Copyright © 2015 Hangfire OÜ.
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

using System.ComponentModel;

namespace Masterly.NightShift
{
    /// <summary>
    /// The configuration, holding the entry (the storage, activator or other object) the last <c>Use*</c> call set, so
    /// later calls can configure it.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IGlobalConfiguration<out T> : IGlobalConfiguration
    {
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        T Entry { get; }
    }

    /// <summary>
    /// The configuration the <c>Use*</c> extension methods act on; see <see cref="GlobalConfiguration.Configuration"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IGlobalConfiguration
    {
    }
}