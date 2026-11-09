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

namespace Masterly.NightShift.Common;

/// <summary>
/// The exception that is thrown when a job could not
/// be loaded from the storage due to missing or incorrect 
/// information about its type or method.
/// </summary>
[Serializable]
public class JobLoadException(string message, Exception inner) : Exception(message, inner)
{

    /// <summary>
    /// What couldn't be loaded, when <see cref="Storage.InvocationData.DeserializeJob"/> threw the exception;
    /// used to explain why the job failed.
    /// </summary>
    internal JobLoadFailure Failure { get; init; }
}

internal enum JobLoadFailure
{
    /// <summary>Not known, for example when a storage created the exception itself.</summary>
    Unknown,

    /// <summary>The job's type, or its method with the stored parameter types, wasn't found.</summary>
    TypeOrMethod,

    /// <summary>The type and method were found, but an argument couldn't be deserialized.</summary>
    Arguments
}