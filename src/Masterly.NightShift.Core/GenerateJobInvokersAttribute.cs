// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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

namespace Masterly.NightShift
{
    /// <summary>
    /// Asks the Masterly.NightShift source generator to emit a reflection-free <see cref="Common.JobInvoker"/> for every
    /// public method of the type, so jobs can be performed without reflection even when they are created in
    /// another assembly. Methods used in <c>Enqueue</c>, <c>Schedule</c>, <c>AddOrUpdate</c> and similar calls
    /// in the same assembly get invokers automatically and don't need this attribute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, Inherited = false)]
    public sealed class GenerateJobInvokersAttribute : Attribute
    {
    }
}
