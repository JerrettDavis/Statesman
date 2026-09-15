using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Conformance.Tests;

/// <summary>
/// One fact per provider, each seeded with the <b>exact stored shape</b> a release before serializer
/// envelopes wrote, proving that what 0.3.0 put in a consumer's store still reads back, with a null
/// envelope and everything else intact.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a shared conformance suite: there is no shared contract to express here.
/// Each provider's pre-envelope shape is a property of its own storage format, and the point of each
/// fact is the literal bytes, not a behaviour the five providers agree on. Every byte string below
/// was captured by running the pre-envelope tree at <c>df50d12</c> and dumping what it wrote, not
/// hand-written from the model, because a hand-written seed proves only that the test author and the
/// reader agree.
/// </para>
/// <para>
/// The discrimination proof for all five is one edit: add an <c>"envelope":{...}</c> key to the
/// seeded bytes (or a non-null <c>EnvelopeJson</c> value to the seeded row) and the fact's
/// <c>Assert.Null</c> must start failing. A seed that still passes with an envelope present is
/// asserting nothing.
/// </para>
/// </remarks>
public sealed class SerializerEnvelopeLegacyReadTests
{
    private static readonly StateAddress Address =
        new("p22", new StatePath("env/legacy"), StatePartition.Default);

    /// <summary>
    /// The stream hash both the filesystem and the Redis provider derive from
    /// <see cref="StateAddress.Canonical"/>, as captured. Pinned as a literal so a change to
    /// canonicalization fails here rather than silently relocating every seed.
    /// </summary>
    private const string StreamHash =
        "da495eed02702651f54d8a5c18fc1435edcaaeba2057adf2afa33584262897d1";

    /// <summary>
    /// The filesystem provider's pre-envelope <c>head.json</c> and history file, byte for byte. Note
    /// what is absent: there is no <c>envelope</c> key anywhere in it.
    /// </summary>
    private const string LegacyFileJson =
        """{"root":"p22","path":"env/legacy","partition":"default","revision":1,"globalPosition":639250939001053385,"occurredAt":"2026-09-15T18:31:40.1053435+00:00","operation":1,"status":1,"valueType":"System.String","schemaVersion":1,"payload":"AQID","source":"application","metadata":{}}""";

    /// <summary>
    /// The Redis provider's pre-envelope member, byte for byte, as dumped from the history sorted set
    /// and the head string alike. <c>globalPosition</c> is last, which is what the append script's
    /// prefix trim depends on.
    /// </summary>
    private const string LegacyRedisJson =
        """{"root":"p22","path":"env/legacy","partition":"default","revision":1,"occurredAt":"2026-09-15T18:31:40.21152+00:00","operation":1,"status":1,"valueType":"System.String","schemaVersion":1,"payload":"AQID","source":"application","metadata":{},"globalPosition":1}""";

    private static readonly byte[] LegacyPayload = [1, 2, 3];

    [Fact]
    public void The_captured_seeds_are_addressed_at_the_hash_both_providers_derive()
    {
        Assert.Equal(StreamHash, Hash(Address));
    }

    [Fact]
    public async Task The_in_memory_provider_reads_a_record_written_without_an_envelope()
    {
        // The degenerate case, and it is worth stating rather than skipping: the in-memory store
        // holds the StateRecord object itself, so it has no stored byte shape to seed and its
        // pre-envelope record is exactly a commit whose Envelope member was never set, which is what
        // every 0.3.0 caller constructed.
        await using var store = new InMemoryStateLedgerStore("legacy", TimeProvider.System);
        Assert.True((await store.AppendAsync(
            Address, StateWriteCondition.Absent, LegacyCommit(), TestContext.Current.CancellationToken)).Succeeded);

        StateRecord? head = await store.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Null(head.Envelope);
        Assert.Equal(LegacyPayload, head.Payload);
    }

    [Fact]
    public async Task The_filesystem_provider_reads_the_bytes_the_pre_envelope_release_wrote()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            SeedFileSystem(directory);
            await using var store = new FileSystemStateLedgerStore(
                "legacy",
                new FileSystemStateLedgerStoreOptions { RootDirectory = directory },
                TimeProvider.System);

            StateRecord? head = await store.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
            Assert.NotNull(head);
            Assert.Null(head.Envelope);
            Assert.Equal(LegacyPayload, head.Payload);
            Assert.Equal(1, head.Revision);
            Assert.Equal("System.String", head.ValueType);

            StateRecord history = await SingleHistoryAsync(store);
            Assert.Null(history.Envelope);
            Assert.Equal(LegacyPayload, history.Payload);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_tiered_provider_reads_the_bytes_its_cold_store_already_held()
    {
        // Tiered has no storage format of its own: reads delegate to cold, which is authoritative.
        // Seeding the filesystem cold store with the captured bytes is therefore the only honest
        // version of this fact for this provider.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            SeedFileSystem(directory);
            var cold = new FileSystemStateLedgerStore(
                "legacy-cold",
                new FileSystemStateLedgerStoreOptions { RootDirectory = directory },
                TimeProvider.System);
            var hot = new InMemoryStateLedgerStore("legacy-hot", TimeProvider.System);
            await using var store = new TieredStateLedgerStore("legacy-tiered", hot, cold, ownsStores: true);

            StateRecord? head = await store.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
            Assert.NotNull(head);
            Assert.Null(head.Envelope);
            Assert.Equal(LegacyPayload, head.Payload);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_redis_provider_reads_the_member_the_pre_envelope_release_wrote()
    {
        string? connectionString = Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connectionString), ConformanceProviders.RedisSkipReason);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        string name = $"legacy-{Guid.NewGuid():N}";
        IDatabase database = connection.GetDatabase();
        string stream = $"{RedisTestLayout.Scope(name)}:stream:{StreamHash}";

        // Exactly the three keys one pre-envelope append leaves behind for one address, with the
        // member text unchanged. The feed and partition keys are not needed by a head or history
        // read and are deliberately not seeded, so this fact says nothing it did not measure.
        await database.StringSetAsync($"{stream}:head", LegacyRedisJson);
        await database.StringSetAsync($"{stream}:revision", 1);
        await database.SortedSetAddAsync($"{stream}:history", LegacyRedisJson, 1);

        await using var store = new RedisStateLedgerStore(name, connection, RedisTestLayout.Options());

        StateRecord? head = await store.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Null(head.Envelope);
        Assert.Equal(LegacyPayload, head.Payload);
        Assert.Equal(1, head.GlobalPosition);

        StateRecord history = await SingleHistoryAsync(store);
        Assert.Null(history.Envelope);
        Assert.Equal(LegacyPayload, history.Payload);
    }

    [Fact]
    public async Task The_entity_framework_provider_reads_a_row_whose_envelope_column_is_null()
    {
        // The migration adds EnvelopeJson as a nullable column, so every row a pre-envelope release
        // wrote has it NULL. This seed inserts exactly the eighteen columns that release knew about
        // and leaves the nineteenth to its default, which is what the migration does to existing
        // rows.
        await using EntityFrameworkTestDatabase database =
            await EntityFrameworkTestDatabase.CreateAsync(EntityFrameworkTestConcurrency.SingleConnection);
        TestDbContextFactory<ConformanceProviders.ConformanceLedgerContext> factory =
            await database.CreateFactoryAsync<ConformanceProviders.ConformanceLedgerContext>(
                options => new ConformanceProviders.ConformanceLedgerContext(options));
        await using var store = new EntityFrameworkStateLedgerStore<ConformanceProviders.ConformanceLedgerContext>(
            "legacy", factory, TimeProvider.System);

        await using (ConformanceProviders.ConformanceLedgerContext context =
            await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            DbConnection connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
            }

            foreach (string table in new[] { "StatesmanHeads", "StatesmanRecords" })
            {
                // Every identifier is double quoted. PostgreSQL folds an unquoted identifier to
                // lower case, so the unquoted form reads `relation "statesmanheads" does not exist`
                // against a model whose tables are PascalCase; SQL Server and SQLite both accept the
                // quoted form unchanged. Measured on all three engines.
                string[] columns =
                [
                    "Root", "Path", "Partition", "Revision", "GlobalPosition", "OccurredAt",
                    "Operation", "Status", "ValueType", "SchemaVersion", "Payload", "Source",
                    "MetadataJson",
                ];
                DbCommand command = connection.CreateCommand();
                command.CommandText =
                    $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) VALUES "
                    + "(@root, @path, @partition, @revision, @position, @occurredAt, @operation, @status, "
                    + "@valueType, @schemaVersion, @payload, @source, @metadataJson)";
                Add(command, "@root", "p22");
                Add(command, "@path", "env/legacy");
                Add(command, "@partition", "default");
                Add(command, "@revision", 1L);
                Add(command, "@position", 1L);
                Add(command, "@occurredAt", new DateTimeOffset(2026, 9, 15, 18, 31, 39, TimeSpan.Zero));
                Add(command, "@operation", 1);
                Add(command, "@status", 1);
                Add(command, "@valueType", "System.String");
                Add(command, "@schemaVersion", 1);
                Add(command, "@payload", LegacyPayload);
                Add(command, "@source", "application");
                Add(command, "@metadataJson", "{}");
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        StateRecord? head = await store.ReadLatestAsync(Address, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Null(head.Envelope);
        Assert.Equal(LegacyPayload, head.Payload);

        StateRecord history = await SingleHistoryAsync(store);
        Assert.Null(history.Envelope);
    }

    /// <summary>
    /// Double quotes one identifier. PostgreSQL folds an unquoted identifier to lower case, so the
    /// unquoted form of this INSERT reads <c>relation "statesmanheads" does not exist</c> against a
    /// model whose tables are PascalCase; SQL Server and SQLite both accept the quoted form
    /// unchanged. Measured on all three engines.
    /// </summary>
    /// <param name="identifier">The table or column name.</param>
    /// <returns>The identifier, double quoted.</returns>
    private static string Quote(string identifier) => "\"" + identifier + "\"";

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void SeedFileSystem(string root)
    {
        string streamDirectory = Path.Combine(root, StreamHash[..2], StreamHash);
        Directory.CreateDirectory(Path.Combine(streamDirectory, "history"));
        File.WriteAllText(Path.Combine(streamDirectory, "head.json"), LegacyFileJson);
        File.WriteAllText(
            Path.Combine(streamDirectory, "history", "00000000000000000001.json"),
            LegacyFileJson);
        File.WriteAllText(
            Path.Combine(root, "_changes.log"),
            "639250939001053385\tp22\tenv/legacy\tdefault\t1\n");
    }

    private static async Task<StateRecord> SingleHistoryAsync(IStateLedgerStore store)
    {
        var records = new List<StateRecord>();
        await foreach (StateRecord record in store.ReadHistoryAsync(
            Address, new StateHistoryOptions(), TestContext.Current.CancellationToken))
        {
            records.Add(record);
        }

        return Assert.Single(records);
    }

    private static string Hash(StateAddress address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Canonical))).ToLowerInvariant();

    private static StateCommit LegacyCommit() => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = "System.String",
        SchemaVersion = 1,
        Payload = LegacyPayload,
        Source = "application",
    };
}
