using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// Column sizes of the schema that bound what the storage can write. Values are checked before they are
    /// written, so a value that doesn't fit fails with a clear message instead of failing deep in SQL Server
    /// or being cut short silently (a cut value is stored under a different name, and later lookups miss it).
    /// </summary>
    internal static class SqlServerLimits
    {
        /// <summary>The <c>[Key]</c> column of <c>[Counter]</c>, <c>[AggregatedCounter]</c>, <c>[Hash]</c>, <c>[List]</c> and <c>[Set]</c>.</summary>
        public const int KeyLength = 100;

        /// <summary>The <c>[Value]</c> column of <c>[Set]</c>.</summary>
        public const int SetValueLength = 256;

        /// <summary>The <c>[Field]</c> column of <c>[Hash]</c>.</summary>
        public const int HashFieldLength = 100;

        /// <summary>The <c>[Name]</c> column of <c>[JobParameter]</c>.</summary>
        public const int JobParameterNameLength = 40;

        /// <summary>The <c>[Name]</c> column of <c>[State]</c> and <c>[StateName]</c> of <c>[Job]</c>.</summary>
        public const int StateNameLength = 20;

        /// <summary>The <c>[Queue]</c> column of <c>[JobQueue]</c>.</summary>
        public const int QueueNameLength = 50;

        /// <summary>The <c>[Id]</c> column of <c>[Server]</c>.</summary>
        public const int ServerIdLength = 200;

        /// <summary>The <c>@Resource</c> parameter of <c>sp_getapplock</c> and <c>sp_releaseapplock</c>.</summary>
        public const int LockResourceLength = 255;

        // Enough of a key or name to see which feature wrote it, without echoing a whole value
        private const int ShownPrefixLength = 40;

        // Hexadecimal digits of the hash that ends a shortened lock resource
        private const int LockResourceHashLength = 16;

        // The range of the datetime columns that hold expiration times
        private static readonly DateTime MinExpireAt = new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime MaxExpireAt = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The expiration time <paramref name="expireIn"/> from now, kept within the range of the <c>ExpireAt</c>
        /// columns. A longer time (for example <see cref="TimeSpan.MaxValue"/>) would overflow <see cref="DateTime"/>
        /// and fail the whole transaction, so it becomes the last day the columns can hold: the data never expires.
        /// </summary>
        public static DateTime GetExpireAt(TimeSpan expireIn)
        {
            return GetExpireAt(DateTime.UtcNow, expireIn);
        }

        /// <summary>The expiration time <paramref name="expireIn"/> after <paramref name="from"/>, kept within the range of the <c>ExpireAt</c> columns.</summary>
        public static DateTime GetExpireAt(DateTime from, TimeSpan expireIn)
        {
            if (expireIn >= MaxExpireAt - from) return MaxExpireAt;
            if (expireIn <= MinExpireAt - from) return MinExpireAt;

            return from.Add(expireIn);
        }

        /// <summary>
        /// The name passed to <c>sp_getapplock</c> for a lock resource. SQL Server takes at most 255 characters and
        /// SqlClient cuts a longer value silently, so resources sharing their first 255 characters would become one
        /// lock. A longer resource keeps a readable prefix followed by <c>~</c> and 16 hexadecimal digits of a hash
        /// of the whole name; names that fit are used as they are.
        /// </summary>
        public static string GetLockResource(string resource)
        {
            if (resource.Length <= LockResourceLength) return resource;

            // "{prefix}~{hash}" is exactly LockResourceLength characters long; a surrogate pair is never cut in two
            var prefixLength = LockResourceLength - LockResourceHashLength - 1;
            if (Char.IsHighSurrogate(resource[prefixLength - 1])) prefixLength--;

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(resource));
                var builder = new StringBuilder(LockResourceLength);
                builder.Append(resource, 0, prefixLength).Append('~');

                for (var i = 0; i < LockResourceHashLength / 2; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public static void EnsureKeyFits(string key)
        {
            if (key.Length > KeyLength)
            {
                throw new ArgumentException(
                    $"The key '{Shorten(key)}' is {key.Length} characters long, but the SQL Server storage keeps keys of at most {KeyLength} characters.",
                    nameof(key));
            }
        }

        public static void EnsureSetValueFits(string key, string value, string paramName = "value")
        {
            if (value.Length > SetValueLength)
            {
                throw new ArgumentException(
                    $"A value of {value.Length} characters can't be used with the set '{Shorten(key)}': the SQL Server storage keeps set values of at most {SetValueLength} characters.",
                    paramName);
            }
        }

        public static void EnsureHashFieldFits(string key, string field)
        {
            if (field == null) throw new ArgumentNullException(nameof(field), $"A hash field name can't be null (hash '{Shorten(key)}').");

            if (field.Length > HashFieldLength)
            {
                throw new ArgumentException(
                    $"The field '{Shorten(field)}' of the hash '{Shorten(key)}' is {field.Length} characters long, but the SQL Server storage keeps hash field names of at most {HashFieldLength} characters.",
                    nameof(field));
            }
        }

        public static void EnsureJobParameterNameFits(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name), "A job parameter name can't be null.");

            if (name.Length > JobParameterNameLength)
            {
                throw new ArgumentException(
                    $"The job parameter name '{Shorten(name)}' is {name.Length} characters long, but the SQL Server storage keeps job parameter names of at most {JobParameterNameLength} characters.",
                    nameof(name));
            }
        }

        public static void EnsureStateNameFits(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name), "A state name can't be null.");

            if (name.Length > StateNameLength)
            {
                throw new ArgumentException(
                    $"The state name '{Shorten(name)}' is {name.Length} characters long, but the SQL Server storage keeps state names of at most {StateNameLength} characters.",
                    nameof(name));
            }
        }

        public static void EnsureQueueNameFits(string queue)
        {
            if (queue.Length > QueueNameLength)
            {
                throw new ArgumentException(
                    $"The queue name '{Shorten(queue)}' is {queue.Length} characters long, but the SQL Server storage keeps queue names of at most {QueueNameLength} characters.",
                    nameof(queue));
            }
        }

        public static void EnsureServerIdFits(string serverId)
        {
            if (serverId.Length > ServerIdLength)
            {
                // Server ids are "{ServerName}:{process id}:{GUID}", so the server name is what makes one too long
                throw new ArgumentException(
                    $"The server id '{Shorten(serverId)}' is {serverId.Length} characters long, but the SQL Server storage keeps server ids of at most {ServerIdLength} characters. A server id is the server name followed by the process id and a GUID (up to 48 characters): set a BackgroundJobServerOptions.ServerName of at most 150 characters.",
                    nameof(serverId));
            }
        }

        private static string Shorten(string value)
        {
            return value.Length <= ShownPrefixLength ? value : value.Substring(0, ShownPrefixLength) + "...";
        }
    }
}
