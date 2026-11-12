using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>
/// Column sizes of the model (<c>ApplyNightShiftModel</c>) that bound what the storage can write. Values are
/// checked before anything is written, so a value that doesn't fit fails with a clear message on every provider:
/// SQLite would store it whole, while SQL Server, PostgreSQL and MySQL refuse it deep in <c>SaveChanges</c>.
/// </summary>
internal static class EfLimits
{
    /// <summary>The <c>Key</c> column of <c>Counter</c>, <c>AggregatedCounter</c>, <c>Hash</c>, <c>List</c> and <c>Set</c>.</summary>
    public const int KeyLength = 100;

    /// <summary>The <c>Value</c> column of <c>Set</c>.</summary>
    public const int SetValueLength = 256;

    /// <summary>The <c>Field</c> column of <c>Hash</c>.</summary>
    public const int HashFieldLength = 100;

    /// <summary>The <c>Name</c> column of <c>JobParameter</c>.</summary>
    public const int JobParameterNameLength = 40;

    /// <summary>The <c>Name</c> column of <c>State</c> and <c>StateName</c> of <c>Job</c>.</summary>
    public const int StateNameLength = 20;

    /// <summary>The <c>Reason</c> column of <c>State</c> and <c>StateReason</c> of <c>Job</c>; longer reasons are cut.</summary>
    public const int StateReasonLength = 100;

    /// <summary>The <c>Queue</c> column of <c>JobQueue</c>.</summary>
    public const int QueueNameLength = 50;

    /// <summary>The <c>Id</c> column of <c>Server</c>.</summary>
    public const int ServerIdLength = 200;

    /// <summary>The <c>Resource</c> column of <c>Lock</c>.</summary>
    public const int LockResourceLength = 255;

    // Enough of a key or name to see which feature wrote it, without echoing a whole value
    private const int ShownPrefixLength = 40;

    // Hexadecimal digits of the hash that ends a shortened lock resource
    private const int LockResourceHashLength = 16;

    /// <summary>
    /// The resource a lock is stored under. A resource longer than the column keeps a readable prefix followed by
    /// <c>~</c> and 16 hexadecimal digits of a hash of the whole name, as on SQL Server, so two long resources
    /// that share their beginning stay two locks; names that fit are used as they are.
    /// </summary>
    public static string LockResource(string resource)
    {
        if (resource.Length <= LockResourceLength)
        {
            return resource;
        }

        // "{prefix}~{hash}" is exactly LockResourceLength characters long; a surrogate pair is never cut in two
        int prefixLength = LockResourceLength - LockResourceHashLength - 1;
        if (char.IsHighSurrogate(resource[prefixLength - 1]))
        {
            prefixLength--;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(resource));
        StringBuilder builder = new(LockResourceLength);
        builder.Append(resource, 0, prefixLength).Append('~');

        for (int i = 0; i < LockResourceHashLength / 2; i++)
        {
            builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Checks every value the operations would write, before any of them is written.</summary>
    /// <exception cref="ArgumentException">A key, value, field or name is longer than its column.</exception>
    public static void EnsureFits(IEnumerable<StorageOperation> operations)
    {
        foreach (StorageOperation operation in operations)
        {
            EnsureFits(operation);
        }
    }

    public static void EnsureJobParameterNameFits(string name)
    {
        if (name.Length > JobParameterNameLength)
        {
            throw new ArgumentException(
                $"The job parameter name '{Shorten(name)}' is {name.Length} characters long, but the Entity Framework Core storage keeps job parameter names of at most {JobParameterNameLength} characters.",
                nameof(name));
        }
    }

    public static void EnsureServerIdFits(string serverId)
    {
        if (serverId.Length > ServerIdLength)
        {
            // Server ids are "{ServerName}:{process id}:{GUID}", so the server name is what makes one too long
            throw new ArgumentException(
                $"The server id '{Shorten(serverId)}' is {serverId.Length} characters long, but the Entity Framework Core storage keeps server ids of at most {ServerIdLength} characters. A server id is the server name followed by the process id and a GUID (up to 48 characters): set a BackgroundJobServerOptions.ServerName of at most 150 characters.",
                nameof(serverId));
        }
    }

    private static void EnsureFits(StorageOperation operation)
    {
        switch (operation)
        {
            case AddJobStateOperation state:
                EnsureStateNameFits(state.State.Name);
                break;
            case AddToQueueOperation queue:
                EnsureQueueNameFits(queue.Queue);
                break;
            case SetJobParameterOperation parameter:
                EnsureJobParameterNameFits(parameter.Name);
                break;
            case CounterOperation counter:
                EnsureKeyFits(counter.Key);
                break;
            case AddToSetOperation add:
                EnsureKeyFits(add.Key);
                EnsureSetValueFits(add.Key, add.Value);
                break;
            case AddRangeToSetOperation range:
                EnsureKeyFits(range.Key);
                foreach (string value in range.Values)
                {
                    EnsureSetValueFits(range.Key, value);
                }

                break;
            case InsertToListOperation insert:
                EnsureKeyFits(insert.Key);
                break;
            case SetRangeInHashOperation hash:
                EnsureKeyFits(hash.Key);
                foreach (KeyValuePair<string, string> field in hash.Fields)
                {
                    EnsureHashFieldFits(hash.Key, field.Key);
                }

                break;
        }
    }

    private static void EnsureKeyFits(string key)
    {
        if (key.Length > KeyLength)
        {
            throw new ArgumentException(
                $"The key '{Shorten(key)}' is {key.Length} characters long, but the Entity Framework Core storage keeps keys of at most {KeyLength} characters.",
                nameof(key));
        }
    }

    private static void EnsureSetValueFits(string key, string value)
    {
        if (value.Length > SetValueLength)
        {
            throw new ArgumentException(
                $"A value of {value.Length} characters can't be used with the set '{Shorten(key)}': the Entity Framework Core storage keeps set values of at most {SetValueLength} characters.",
                nameof(value));
        }
    }

    private static void EnsureHashFieldFits(string key, string field)
    {
        if (field == null)
        {
            throw new ArgumentNullException(nameof(field), $"A hash field name can't be null (hash '{Shorten(key)}').");
        }

        if (field.Length > HashFieldLength)
        {
            throw new ArgumentException(
                $"The field '{Shorten(field)}' of the hash '{Shorten(key)}' is {field.Length} characters long, but the Entity Framework Core storage keeps hash field names of at most {HashFieldLength} characters.",
                nameof(field));
        }
    }

    private static void EnsureStateNameFits(string name)
    {
        if (name.Length > StateNameLength)
        {
            throw new ArgumentException(
                $"The state name '{Shorten(name)}' is {name.Length} characters long, but the Entity Framework Core storage keeps state names of at most {StateNameLength} characters.",
                nameof(name));
        }
    }

    private static void EnsureQueueNameFits(string queue)
    {
        if (queue.Length > QueueNameLength)
        {
            throw new ArgumentException(
                $"The queue name '{Shorten(queue)}' is {queue.Length} characters long, but the Entity Framework Core storage keeps queue names of at most {QueueNameLength} characters.",
                nameof(queue));
        }
    }

    private static string Shorten(string value)
    {
        return value.Length <= ShownPrefixLength ? value : string.Concat(value.AsSpan(0, ShownPrefixLength), "...");
    }
}
