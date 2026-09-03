using Microsoft.EntityFrameworkCore;

namespace Statesman;

public class StatesmanLedgerDbContext : DbContext
{
    public StatesmanLedgerDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<StatesmanLedgerHead> StatesmanHeads => Set<StatesmanLedgerHead>();

    public DbSet<StatesmanLedgerRecord> StatesmanRecords => Set<StatesmanLedgerRecord>();

    public DbSet<StatesmanLedgerSequence> StatesmanSequences => Set<StatesmanLedgerSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StatesmanLedgerHead>(entity =>
        {
            entity.ToTable("StatesmanHeads");
            entity.HasKey(value => new { value.Root, value.Path, value.Partition });
            entity.Property(value => value.Root).HasMaxLength(128);
            entity.Property(value => value.Path).HasMaxLength(512);
            entity.Property(value => value.Partition).HasMaxLength(256);
            entity.Property(value => value.ValueType).HasMaxLength(1024);
            entity.Property(value => value.Source).HasMaxLength(256);
            entity.Property(value => value.Revision).IsConcurrencyToken();
        });

        modelBuilder.Entity<StatesmanLedgerRecord>(entity =>
        {
            entity.ToTable("StatesmanRecords");
            entity.HasKey(value => new { value.Root, value.Path, value.Partition, value.Revision });
            entity.HasIndex(value => value.GlobalPosition).IsUnique();
            entity.Property(value => value.Root).HasMaxLength(128);
            entity.Property(value => value.Path).HasMaxLength(512);
            entity.Property(value => value.Partition).HasMaxLength(256);
            entity.Property(value => value.ValueType).HasMaxLength(1024);
            entity.Property(value => value.Source).HasMaxLength(256);
        });

        modelBuilder.Entity<StatesmanLedgerSequence>(entity =>
        {
            entity.ToTable("StatesmanSequences");
            entity.HasKey(value => value.Name);
            entity.Property(value => value.Name).HasMaxLength(128);
            entity.Property(value => value.Value).IsConcurrencyToken();
        });
    }
}

public sealed class StatesmanLedgerHead
{
    public string Root { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Partition { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long GlobalPosition { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public StateOperation Operation { get; set; }
    public StateStatus Status { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public byte[]? Payload { get; set; }
    public DateTimeOffset? FreshUntil { get; set; }
    public DateTimeOffset? ServeUntil { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string? ErrorJson { get; set; }
}

public sealed class StatesmanLedgerRecord
{
    public string Root { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Partition { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long GlobalPosition { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public StateOperation Operation { get; set; }
    public StateStatus Status { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public byte[]? Payload { get; set; }
    public DateTimeOffset? FreshUntil { get; set; }
    public DateTimeOffset? ServeUntil { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string? ErrorJson { get; set; }
}

public sealed class StatesmanLedgerSequence
{
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
}
