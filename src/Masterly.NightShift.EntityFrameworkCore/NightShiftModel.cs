using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Masterly.NightShift.EntityFrameworkCore;

// The rows of the storage. The current state is kept on the job row, so a job never points at a state row and
// the job and its first state insert together without a circular dependency.

internal sealed class JobRow
{
    public long Id { get; set; }

    public string InvocationData { get; set; } = null!;

    public string? Arguments { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpireAt { get; set; }

    public string? StateName { get; set; }

    public string? StateReason { get; set; }

    public string? StateData { get; set; }

    public DateTime? StateChangedAt { get; set; }

    public List<StateRow> History { get; set; } = [];

    public List<JobParameterRow> Parameters { get; set; } = [];
}

internal sealed class StateRow
{
    public long Id { get; set; }

    public long JobId { get; set; }

    public string Name { get; set; } = null!;

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? Data { get; set; }
}

internal sealed class JobParameterRow
{
    public long JobId { get; set; }

    public string Name { get; set; } = null!;

    public string? Value { get; set; }
}

internal sealed class JobQueueRow
{
    public long Id { get; set; }

    public string Queue { get; set; } = null!;

    public long JobId { get; set; }

    public DateTime? FetchedAt { get; set; }

    public string? FetchToken { get; set; }
}

internal sealed class SetRow
{
    public string Key { get; set; } = null!;

    public string Value { get; set; } = null!;

    public double Score { get; set; }

    public DateTime? ExpireAt { get; set; }
}

internal sealed class ListRow
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public string Value { get; set; } = null!;

    public DateTime? ExpireAt { get; set; }
}

internal sealed class HashRow
{
    public string Key { get; set; } = null!;

    public string Field { get; set; } = null!;

    public string? Value { get; set; }

    public DateTime? ExpireAt { get; set; }
}

internal sealed class CounterRow
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public long Value { get; set; }

    public DateTime? ExpireAt { get; set; }
}

internal sealed class AggregatedCounterRow
{
    public string Key { get; set; } = null!;

    public long Value { get; set; }

    public DateTime? ExpireAt { get; set; }
}

internal sealed class ServerRow
{
    public string Id { get; set; } = null!;

    public string Data { get; set; } = null!;

    public DateTime LastHeartbeat { get; set; }
}

internal sealed class LockRow
{
    public string Resource { get; set; } = null!;

    public string Owner { get; set; } = null!;

    public DateTime ExpireAt { get; set; }
}

/// <summary>
/// Adds the storage's tables to a model: <c>modelBuilder.ApplyNightShiftModel()</c> in the
/// <c>OnModelCreating</c> of an application's context, so its migrations create the tables and
/// <see cref="EntityFrameworkCoreStorage"/> can use the context.
/// </summary>
/// <remarks>
/// The application owns these tables and their upgrades: when a new version of the package changes the model (its
/// changelog says so), add a migration (<c>dotnet ef migrations add</c>) and apply it before the new version
/// runs, as for a change to the application's own entities. A dedicated context instead records the version of
/// its tables and refuses tables of another version.
/// </remarks>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class NightShiftModelBuilderExtensions
#pragma warning restore CA1708
{
    /// <summary>The schema the tables are in unless another one is given.</summary>
    public const string DefaultSchema = "nightshift";

    /// <param name="modelBuilder">The model builder of the context.</param>
    extension([NotNull] ModelBuilder modelBuilder)
    {
        /// <summary>Adds the storage's tables, in <paramref name="schema"/> (<c>nightshift</c> by default).</summary>
        /// <param name="schema">The schema of the tables, or <c>null</c> for the database's default schema (SQLite has no schemas).</param>
        /// <returns>The model builder, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is <c>null</c>.</exception>
        public ModelBuilder ApplyNightShiftModel([CanBeNull] string? schema = DefaultSchema)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.Entity<JobRow>(job =>
            {
                job.ToTable("Job", schema).HasKey(static x => x.Id);
                job.Property(static x => x.InvocationData).IsRequired();
                job.Property(static x => x.StateName).HasMaxLength(EfLimits.StateNameLength);
                job.Property(static x => x.StateReason).HasMaxLength(EfLimits.StateReasonLength);
                job.HasIndex(static x => new { x.StateName, x.Id });
                job.HasIndex(static x => x.ExpireAt);
                job.HasMany(static x => x.History).WithOne().HasForeignKey(static x => x.JobId).OnDelete(DeleteBehavior.Cascade);
                job.HasMany(static x => x.Parameters).WithOne().HasForeignKey(static x => x.JobId).OnDelete(DeleteBehavior.Cascade);
                Utc(job.Property(static x => x.CreatedAt));
                Utc(job.Property(static x => x.ExpireAt));
                Utc(job.Property(static x => x.StateChangedAt));
            });

            modelBuilder.Entity<StateRow>(state =>
            {
                state.ToTable("State", schema).HasKey(static x => x.Id);
                state.Property(static x => x.Name).HasMaxLength(EfLimits.StateNameLength).IsRequired();
                state.Property(static x => x.Reason).HasMaxLength(EfLimits.StateReasonLength);
                state.HasIndex(static x => new { x.JobId, x.Id });
                Utc(state.Property(static x => x.CreatedAt));
            });

            modelBuilder.Entity<JobParameterRow>(parameter =>
            {
                parameter.ToTable("JobParameter", schema).HasKey(static x => new { x.JobId, x.Name });
                parameter.Property(static x => x.Name).HasMaxLength(EfLimits.JobParameterNameLength);
            });

            modelBuilder.Entity<JobQueueRow>(queue =>
            {
                queue.ToTable("JobQueue", schema).HasKey(static x => x.Id);
                queue.Property(static x => x.Queue).HasMaxLength(EfLimits.QueueNameLength).IsRequired();
                queue.Property(static x => x.FetchToken).HasMaxLength(32);
                queue.HasIndex(static x => new { x.Queue, x.FetchedAt, x.Id });
                queue.HasIndex(static x => x.JobId);
                Utc(queue.Property(static x => x.FetchedAt));
            });

            modelBuilder.Entity<SetRow>(set =>
            {
                set.ToTable("Set", schema).HasKey(static x => new { x.Key, x.Value });
                set.Property(static x => x.Key).HasMaxLength(EfLimits.KeyLength);
                set.Property(static x => x.Value).HasMaxLength(EfLimits.SetValueLength);
                set.HasIndex(static x => new { x.Key, x.Score, x.Value });
                set.HasIndex(static x => x.ExpireAt);
                Utc(set.Property(static x => x.ExpireAt));
            });

            modelBuilder.Entity<ListRow>(list =>
            {
                list.ToTable("List", schema).HasKey(static x => x.Id);
                list.Property(static x => x.Key).HasMaxLength(EfLimits.KeyLength).IsRequired();
                list.Property(static x => x.Value).IsRequired();
                list.HasIndex(static x => new { x.Key, x.Id });
                list.HasIndex(static x => x.ExpireAt);
                Utc(list.Property(static x => x.ExpireAt));
            });

            modelBuilder.Entity<HashRow>(hash =>
            {
                hash.ToTable("Hash", schema).HasKey(static x => new { x.Key, x.Field });
                hash.Property(static x => x.Key).HasMaxLength(EfLimits.KeyLength);
                hash.Property(static x => x.Field).HasMaxLength(EfLimits.HashFieldLength);
                hash.HasIndex(static x => x.ExpireAt);
                Utc(hash.Property(static x => x.ExpireAt));
            });

            modelBuilder.Entity<CounterRow>(counter =>
            {
                counter.ToTable("Counter", schema).HasKey(static x => x.Id);
                counter.Property(static x => x.Key).HasMaxLength(EfLimits.KeyLength).IsRequired();
                counter.HasIndex(static x => x.Key);
                Utc(counter.Property(static x => x.ExpireAt));
            });

            modelBuilder.Entity<AggregatedCounterRow>(counter =>
            {
                counter.ToTable("AggregatedCounter", schema).HasKey(static x => x.Key);
                counter.Property(static x => x.Key).HasMaxLength(EfLimits.KeyLength);
                counter.HasIndex(static x => x.ExpireAt);
                Utc(counter.Property(static x => x.ExpireAt));
            });

            modelBuilder.Entity<ServerRow>(server =>
            {
                server.ToTable("Server", schema).HasKey(static x => x.Id);
                server.Property(static x => x.Id).HasMaxLength(EfLimits.ServerIdLength);
                server.Property(static x => x.Data).IsRequired();
                Utc(server.Property(static x => x.LastHeartbeat));
            });

            modelBuilder.Entity<LockRow>(row =>
            {
                row.ToTable("Lock", schema).HasKey(static x => x.Resource);
                row.Property(static x => x.Resource).HasMaxLength(EfLimits.LockResourceLength);
                row.Property(static x => x.Owner).HasMaxLength(32).IsRequired();
                Utc(row.Property(static x => x.ExpireAt));
            });

            return modelBuilder;
        }
    }

    // Times are written as UTC and read back as UTC: providers return DateTime values without a kind
    private static void Utc(PropertyBuilder<DateTime> property)
    {
        property.HasConversion(static x => x, static x => DateTime.SpecifyKind(x, DateTimeKind.Utc));
    }

    private static void Utc(PropertyBuilder<DateTime?> property)
    {
        property.HasConversion(static x => x, static x => x.HasValue ? DateTime.SpecifyKind(x.Value, DateTimeKind.Utc) : x);
    }
}

/// <summary>The version of the tables a dedicated context created, in a table of one row.</summary>
internal sealed class SchemaVersionRow
{
    /// <summary>
    /// The version of the tables of a dedicated context. Raise it with every change to the model, and teach
    /// <c>EntityFrameworkCoreStorage.PrepareSchema</c> to upgrade the version before it.
    /// </summary>
    public const int Current = 1;

    /// <summary>The name of the table.</summary>
    public const string TableName = "SchemaVersion";

    public int Version { get; set; }
}

/// <summary>The context the storage uses when it has a database of its own.</summary>
internal sealed class NightShiftDbContext(DbContextOptions options, string? schema) : DbContext(options)
{
    public string? Schema { get; } = schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyNightShiftModel(Schema);

        // Only the dedicated context records its version: an application's migrations track its own context
        modelBuilder.Entity<SchemaVersionRow>(version =>
        {
            version.ToTable(SchemaVersionRow.TableName, Schema).HasKey(static x => x.Version);
            version.Property(static x => x.Version).ValueGeneratedNever();
        });
    }
}

/// <summary>Builds one model per schema, since the dedicated context's model depends on it.</summary>
internal sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return (context.GetType(), (context as NightShiftDbContext)?.Schema, designTime);
    }
}
