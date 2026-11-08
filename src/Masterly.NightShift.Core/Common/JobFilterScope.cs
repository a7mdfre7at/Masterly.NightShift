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

using Masterly.NightShift.Client;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Defines values that specify the order in which Masterly.NightShift filters 
    /// run within the same filter type and filter order.
    /// </summary>
    /// 
    /// <remarks>
    /// Masterly.NightShift supports the following types of filters:
    /// 
    /// <list type="number">
    ///     <item>
    ///         <description>
    ///             Client / Server filters, which implement
    ///             <see cref="IClientFilter"/> and <see cref="IServerFilter"/>
    ///             interfaces respectively.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             State changing filters, which implement the
    ///             <see cref="IElectStateFilter"/> interface.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             State changed filters, which implement the
    ///             <see cref="IApplyStateFilter"/> interface.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             Client / Server exception filters, which implement
    ///             <see cref="IClientExceptionFilter"/> or 
    ///             <see cref="IServerExceptionFilter"/> interfaces
    ///             respectively.
    ///         </description>
    ///     </item>
    /// </list>
    /// 
    /// The order in which these filter types run is fixed: for example,
    /// exception filters always run after all other filters, and state
    /// filters always run inside the client and server filters.
    ///
    /// Within one filter type, the execution order is determined first by
    /// the Order value and then by the Scope value. The <see cref="JobFilterScope"/>
    /// enumeration defines the following values (in the order they run):
    /// 
    /// <list type="number">
    ///     <item>
    ///         <description>
    ///             <see cref="JobFilterScope.Global"/>.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             <see cref="Type"/>.
    ///         </description>
    ///     </item>
    ///     <item>
    ///         <description>
    ///             <see cref="Method"/>.
    ///         </description>
    ///     </item>
    /// </list>
    /// 
    /// For example, a client filter whose Order property is 0 and whose
    /// filter scope is <see cref="JobFilterScope.Global"/> runs before a
    /// filter with the same Order value but with the <see cref="Type"/>
    /// filter scope.
    ///
    /// Scope values are mostly set by implementations of the
    /// <see cref="IJobFilterProvider"/> interface. For example, the
    /// <see cref="JobFilterCollection"/> class sets the scope to
    /// <see cref="JobFilterScope.Global"/>.
    ///
    /// The execution order of filters of the same type, with the same
    /// Order value and the same scope, is undefined.
    /// </remarks>
    public enum JobFilterScope
    {
        /// <summary>
        /// Specifies an order before the <see cref="Type"/>.
        /// </summary>
        Global = 10,

        /// <summary>
        /// Specifies an order after the <see cref="Global"/> and
        /// before the <see cref="Method"/>.
        /// </summary>
        Type = 20,

        /// <summary>
        /// Specifies an order after the <see cref="Type"/>.
        /// </summary>
        Method = 30,
    }
}