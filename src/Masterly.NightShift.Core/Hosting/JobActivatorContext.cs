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

#nullable enable

using System;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Describes the job a <see cref="JobActivator"/> begins a scope for.
    /// </summary>
    public class JobActivatorContext
    {
        /// <summary>
        /// Initializes the context.
        /// </summary>
        /// <param name="connection">The storage connection the job is performed with.</param>
        /// <param name="backgroundJob">The job about to run.</param>
        /// <param name="cancellationToken">The job's cancellation token.</param>
        public JobActivatorContext(
            [NotNull] IStorageConnection connection,
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IJobCancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (backgroundJob == null) throw new ArgumentNullException(nameof(backgroundJob));
            if (cancellationToken == null) throw new ArgumentNullException(nameof(cancellationToken));

            Connection = connection;
            BackgroundJob = backgroundJob;
            CancellationToken = cancellationToken;
        }

        /// <summary>Gets the job about to run.</summary>
        [NotNull]
        public BackgroundJob BackgroundJob { get; }

        /// <summary>Gets the job's cancellation token.</summary>
        [NotNull]
        public IJobCancellationToken CancellationToken { get; }

        /// <summary>Gets the storage connection the job is performed with.</summary>
        [NotNull]
        public IStorageConnection Connection { get; }

        /// <summary>
        /// Stores a job parameter, serialized with the user serializer settings.
        /// </summary>
        /// <param name="name">The parameter name.</param>
        /// <param name="value">The value, or <c>null</c>.</param>
        public void SetJobParameter([NotNull] string name, object? value)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            Connection.SetJobParameter(BackgroundJob.Id, name, SerializationHelper.Serialize(value, SerializationOption.User));
        }

        /// <summary>
        /// Reads a job parameter from the storage, or returns the default value of <typeparamref name="T"/> when it is missing.
        /// </summary>
        /// <param name="name">The parameter name.</param>
        public T? GetJobParameter<T>([NotNull] string name) => GetJobParameter<T>(name, allowStale: false);

        /// <summary>
        /// Reads a job parameter, or returns the default value of <typeparamref name="T"/> when it is missing.
        /// </summary>
        /// <param name="name">The parameter name.</param>
        /// <param name="allowStale">Whether the parameters read when the job was fetched may be used instead of reading the storage.</param>
        public T? GetJobParameter<T>([NotNull] string name, bool allowStale)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            try
            {
                string? value;

                if (allowStale && BackgroundJob.ParametersSnapshot != null)
                {
                    BackgroundJob.ParametersSnapshot.TryGetValue(name, out value);
                }
                else
                {
                    value = Connection.GetJobParameter(BackgroundJob.Id, name);                
                }

                return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new InvalidOperationException(
                    $"Could not get a value of the job parameter `{name}`. See inner exception for details.", ex);
            }
        }
    }
}