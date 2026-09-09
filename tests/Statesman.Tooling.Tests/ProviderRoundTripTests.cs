using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Statesman.Tooling.Tests;

public sealed class ProviderRoundTripTests
{
    private static string? RedisConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private static readonly StatesmanManifest Manifest = new()
    {
        Id = "app",
        Version = "1.0",
        Fingerprint = "fingerprint-app",
    };

    private static readonly StateAddress AddressA = new("app", "roundtrip/a", StatePartition.Default);
    private static readonly StateAddress AddressB = new("app", "roundtrip/b", new StatePartition("tenant-1"));

    [Fact]
    public async Task FileSystem_export_restores_exactly_into_EntityFrameworkCore()
    {
        string directory = TempDirectory();
        try
        {
            await using var source = new FileSystemStateLedgerStore("files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            Expected expected = await SeedAsync(source);
            byte[] export = await ExportAsync(source);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RoundTripContext>().UseSqlite(connection).Options;
            var factory = new RoundTripContextFactory(options);
            await using (RoundTripContext context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            await using var target = new EntityFrameworkStateLedgerStore<RoundTripContext>("database", factory);
            StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));

            Assert.Equal(3, summary.Records);
            await AssertRestoredAsync(target, expected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EntityFrameworkCore_export_restores_exactly_into_FileSystem()
    {
        string directory = TempDirectory();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RoundTripContext>().UseSqlite(connection).Options;
            var factory = new RoundTripContextFactory(options);
            await using (RoundTripContext context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            await using var source = new EntityFrameworkStateLedgerStore<RoundTripContext>("database", factory);
            Expected expected = await SeedAsync(source);
            byte[] export = await ExportAsync(source);

            await using var target = new FileSystemStateLedgerStore("files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));

            Assert.Equal(3, summary.Records);
            await AssertRestoredAsync(target, expected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InMemory_export_restores_exactly_into_Redis_and_later_appends_allocate_above_imported_positions()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(RedisConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using var source = new InMemoryStateLedgerStore("memory");
        Expected expected = await SeedAsync(source);
        byte[] export = await ExportAsync(source);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString!);
        await using var target = new RedisStateLedgerStore($"tooling-test-{Guid.NewGuid():N}", connection);
        StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));

        Assert.Equal(3, summary.Records);
        await AssertRestoredAsync(target, expected);

        StateAppendResult next = await target.AppendAsync(AddressB, StateWriteCondition.AtRevision(1), Commit("b2"));
        Assert.True(next.Record!.GlobalPosition > expected.MaxPosition,
            $"an append after restore must allocate above every imported position ({expected.MaxPosition}), but got {next.Record.GlobalPosition}");
    }

    [Fact]
    public async Task Redis_export_restores_exactly_into_InMemory()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(RedisConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString!);
        await using var source = new RedisStateLedgerStore($"tooling-test-{Guid.NewGuid():N}", connection);
        Expected expected = await SeedAsync(source);
        byte[] export = await ExportAsync(source);

        await using var target = new InMemoryStateLedgerStore("memory");
        StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export));

        Assert.Equal(3, summary.Records);
        await AssertRestoredAsync(target, expected);
    }

    [Fact]
    public async Task Tiered_target_throws_NotSupportedException_pointing_at_the_cold_store()
    {
        await using var source = new InMemoryStateLedgerStore("memory");
        await SeedAsync(source);
        byte[] export = await ExportAsync(source);

        await using var target = new TieredStateLedgerStore("tiered", new InMemoryStateLedgerStore("hot"), new InMemoryStateLedgerStore("cold"), ownsStores: true);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export)));

        Assert.Contains("IStateLedgerReplica", exception.Message);
        Assert.Contains("cold", exception.Message);
    }

    [Fact]
    public async Task FileSystem_export_is_refused_by_Redis_because_tick_positions_exceed_the_exact_score_range()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(RedisConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        string directory = TempDirectory();
        try
        {
            await using var source = new FileSystemStateLedgerStore("files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            Expected expected = await SeedAsync(source);
            Assert.True(expected.MaxPosition > RedisStateLedgerStore.MaxImportablePosition,
                "the filesystem provider allocates tick-based positions above the Redis bound; this test relies on that");
            byte[] export = await ExportAsync(source);

            await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString!);
            await using var target = new RedisStateLedgerStore($"tooling-test-{Guid.NewGuid():N}", connection);

            NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await StateLedgerRestore.RestoreAsync(target, Manifest, new MemoryStream(export)));

            Assert.Contains("2^52", exception.Message);

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in target.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            Assert.Empty(partitions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record Expected(StateRecord A1, StateRecord A2, StateRecord B1)
    {
        public long MaxPosition => Math.Max(A2.GlobalPosition, B1.GlobalPosition);
    }

    private static async Task<Expected> SeedAsync(IStateLedgerStore store)
    {
        StateAppendResult a1 = await store.AppendAsync(AddressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult b1 = await store.AppendAsync(AddressB, StateWriteCondition.Absent, Commit("b1"));
        StateAppendResult a2 = await store.AppendAsync(AddressA, StateWriteCondition.AtRevision(1), Commit("a2"));
        return new Expected(a1.Record!, a2.Record!, b1.Record!);
    }

    private static async Task<byte[]> ExportAsync(IStateLedgerStore source)
    {
        using var output = new MemoryStream();
        await StateLedgerExport.ExportAsync(source, Manifest, output);
        return output.ToArray();
    }

    private static async Task AssertRestoredAsync(IStateLedgerStore target, Expected expected)
    {
        StateRecord? headA = await target.ReadLatestAsync(AddressA);
        Assert.NotNull(headA);
        Assert.Equal(2, headA!.Revision);
        Assert.Equal(expected.A2.GlobalPosition, headA.GlobalPosition);
        Assert.Equal(expected.A2.OccurredAt, headA.OccurredAt);
        Assert.Equal("a2", Encoding.UTF8.GetString(headA.Payload!));

        StateRecord? headB = await target.ReadLatestAsync(AddressB);
        Assert.NotNull(headB);
        Assert.Equal(1, headB!.Revision);
        Assert.Equal(expected.B1.GlobalPosition, headB.GlobalPosition);

        List<StateRecord> historyA = [];
        await foreach (StateRecord record in target.ReadHistoryAsync(AddressA, new StateHistoryOptions { Take = null, NewestFirst = false }))
        {
            historyA.Add(record);
        }

        Assert.Equal(
            new[] { (1L, expected.A1.GlobalPosition), (2L, expected.A2.GlobalPosition) },
            historyA.Select(record => (record.Revision, record.GlobalPosition)).ToArray());

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in ((IStateChangeFeed)target).ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        Assert.Equal(
            new[] { expected.A1.GlobalPosition, expected.B1.GlobalPosition, expected.A2.GlobalPosition },
            changes.Select(envelope => envelope.Cursor.Position).ToArray());

        List<StatePartitionDescriptor> partitions = [];
        await foreach (StatePartitionDescriptor descriptor in ((IPartitionCatalog)target).ListPartitionsAsync())
        {
            partitions.Add(descriptor);
        }

        Assert.Equal(2, partitions.Count);
        Assert.Equal(expected.A2.GlobalPosition, Assert.Single(partitions, p => p.Address.Equals(AddressA)).LastPosition.Position);
        Assert.Equal(expected.B1.GlobalPosition, Assert.Single(partitions, p => p.Address.Equals(AddressB)).LastPosition.Position);
    }

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed class RoundTripContext : StatesmanLedgerDbContext
    {
        public RoundTripContext(DbContextOptions<RoundTripContext> options)
            : base(options)
        {
        }
    }

    private sealed class RoundTripContextFactory : IDbContextFactory<RoundTripContext>
    {
        private readonly DbContextOptions<RoundTripContext> _options;

        public RoundTripContextFactory(DbContextOptions<RoundTripContext> options)
        {
            _options = options;
        }

        public RoundTripContext CreateDbContext() => new(_options);

        public Task<RoundTripContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
