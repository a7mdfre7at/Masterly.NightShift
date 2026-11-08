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

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift
{
    /// <summary>
    /// Specifies a display name for a job method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class JobDisplayNameAttribute : Attribute
    {
        private static readonly ConcurrentDictionary<Type, ResourceManager> _resourceManagerCache
            = new ConcurrentDictionary<Type, ResourceManager>();

        public JobDisplayNameAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                throw new ArgumentException("Display name is empty", nameof(displayName));

            DisplayName = displayName;
        }

        /// <summary>
        /// Gets display name for the job.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Gets or sets resource type to localize <see cref="DisplayName"/> string.
        /// </summary>
        public Type ResourceType { get; set; }

        /// <summary>
        /// Formats the display name for a job: <c>{0}</c>, <c>{1}</c>... are replaced with the job's arguments,
        /// and a <see cref="RedactedAttribute"/> argument with <see cref="RedactedAttribute.Mask"/>.
        /// </summary>
        /// <param name="context">The dashboard context.</param>
        /// <param name="job">The job to name.</param>
        /// <returns>The display name.</returns>
        public virtual string Format(DashboardContext context, Job job)
        {
            var format = DisplayName;

            if (ResourceType != null)
            {
                format = _resourceManagerCache
                    .GetOrAdd(ResourceType, InitResourceManager)
                    .GetString(DisplayName, CultureInfo.CurrentUICulture);

                if (string.IsNullOrEmpty(format))
                {
                    // failed to localize display name string, use it as is
                    format = DisplayName;
                }
            }
            
            // A [Redacted] argument is shown as RedactedAttribute.Mask, like everywhere else in the dashboard
            return string.Format(CultureInfo.CurrentCulture, format, RedactedAttribute.GetDisplayArguments(job));
        }

        private static ResourceManager InitResourceManager(Type type)
        {
            var prop = type.GetTypeInfo().GetDeclaredProperty("ResourceManager");
            if (prop != null && prop.PropertyType == typeof(ResourceManager) && prop.CanRead && prop.GetMethod.IsStatic)
            {
                // use existing resource manager if possible
                return (ResourceManager)prop.GetValue(null);
            }

            return new ResourceManager(type);
        }
    }
}
