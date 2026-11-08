// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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
    /// Fills a continuation's parameter with the result of its parent job (<c>AntecedentResult</c>), when continuation
    /// results are on.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class FromResultAttribute : FromParameterAttribute
    {
        public FromResultAttribute() : base("AntecedentResult")
        {
        }
    }

    /// <summary>
    /// Fills a continuation's parameter with the exception that deleted its parent job (<c>AntecedentException</c>),
    /// when continuation results are on.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class FromExceptionAttribute : FromParameterAttribute
    {
        public FromExceptionAttribute() : base("AntecedentException")
        {
        }
    }
}