using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Statesman.Redis.Tests;

/// <summary>
/// Live probes for the commit-time append script: the Lua number formatting, the JSON
/// prefix/suffix split, the atomicity of the revision guard, and backward compatibility with the
/// property order earlier versions wrote.
/// </summary>
public sealed class RedisAppendScriptTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public async Task Positions_round_trip_exactly_at_the_maximum_importable_position()
    {
        // string.format('%d', n) is exact to 2^53; tostring(n) is '%.14g' in Redis's Lua 5.1 and
        // mangles integers above 10^14. Importing at 2^52 and appending once past it exercises
        // the score argument, the JSON concatenation, and the reply path at a magnitude where the
        // wrong formatter would visibly corrupt the value. This is the Phase 6 lesson made
        // executable.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"script-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "script/max", StatePartition.Default);
        var imported = new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = RedisStateLedgerStore.MaxImportablePosition,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Imported,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = "value"u8.ToArray(),
            Source = "test",
        };

        await store.ImportAsync(imported);
        StateAppendResult appended = await store.AppendAsync(
            address, StateWriteCondition.AtRevision(1), Commit("next"));

        Assert.True(appended.Succeeded);
        Assert.Equal(1L << 52, imported.GlobalPosition);
        Assert.Equal((1L << 52) + 1, appended.Record!.GlobalPosition);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(1L << 52, changes[0].Record.GlobalPosition);
        Assert.Equal((1L << 52) + 1, changes[1].Record.GlobalPosition);
    }

    [Fact]
    public async Task A_record_whose_metadata_contains_the_position_property_text_round_trips_field_for_field()
    {
        // The prefix/suffix split trims the last two characters of the serialized document. If
        // anything else in the document could be mistaken for the trailing property -- metadata
        // holding the literal text "globalPosition":0, a large payload, a populated error -- the
        // split would corrupt the record silently. Compare the whole record, not just the
        // position.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"script-test-{Guid.NewGuid():N}", connection);
        var address = new StateAddress("app", "script/tricky", StatePartition.Default);
        byte[] payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        var commit = new StateCommit
        {
            Operation = StateOperation.Faulted,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 3,
            Payload = payload,
            Source = "test",
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["trap"] = "\"globalPosition\":0}",
                ["brace"] = "}",
            },
            Error = new StateError("code", "message", "System.InvalidOperationException", "detail", IsTransient: true),
        };

        StateAppendResult appended = await store.AppendAsync(address, StateWriteCondition.Absent, commit);
        Assert.True(appended.Succeeded);

        StateRecord? readBack = await store.ReadLatestAsync(address);

        Assert.NotNull(readBack);
        Assert.Equal(appended.Record!.GlobalPosition, readBack!.GlobalPosition);
        Assert.Equal(appended.Record.Revision, readBack.Revision);
        Assert.Equal(appended.Record.Operation, readBack.Operation);
        Assert.Equal(appended.Record.Status, readBack.Status);
        Assert.Equal(appended.Record.ValueType, readBack.ValueType);
        Assert.Equal(appended.Record.SchemaVersion, readBack.SchemaVersion);
        Assert.Equal(appended.Record.Source, readBack.Source);
        Assert.Equal(appended.Record.CorrelationId, readBack.CorrelationId);
        Assert.Equal(appended.Record.CausationId, readBack.CausationId);
        Assert.Equal(appended.Record.Error, readBack.Error);
        Assert.Equal(payload, readBack.Payload);
        Assert.Equal("\"globalPosition\":0}", readBack.Metadata["trap"]);
        Assert.Equal("}", readBack.Metadata["brace"]);
    }

    [Fact]
    public async Task Concurrent_absent_appends_to_one_address_let_exactly_one_win()
    {
        // The AddCondition/WATCH path is gone, so the revision guard is now a Lua string
        // comparison. nil-versus-empty-string is the classic Lua trap: if the guard compared
        // wrongly, two concurrent creates would both commit and the stream would hold two
        // revision 1s. Repeat it, because a single round can pass by luck.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"script-test-{Guid.NewGuid():N}", connection);

        for (int round = 0; round < 20; round++)
        {
            var address = new StateAddress("app", $"script/race-{round}", StatePartition.Default);
            StateAppendResult[] results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(index =>
                    Task.Run(() => store.AppendAsync(address, StateWriteCondition.Absent, Commit($"v{index}")).AsTask())));

            Assert.Single(results, result => result.Succeeded);

            List<StateRecord> history = [];
            await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions()))
            {
                history.Add(record);
            }

            Assert.Single(history);
        }
    }

    [Fact]
    public async Task A_record_written_with_the_previous_property_order_still_deserializes()
    {
        // Moving globalPosition to the end of RedisRecord changes the order properties are
        // written, not the format: System.Text.Json deserialization is order-independent. Prove
        // it rather than assert it, by writing a document in the pre-Phase-8 order straight into
        // the change-feed sorted set -- whose key is derivable from the prefix and store name --
        // and reading it back through the feed.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"script-test-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(name, connection);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        var legacy = new LegacyRedisRecord(
            Root: "app",
            Path: "script/legacy",
            Partition: StatePartition.Default.Value,
            Revision: 1,
            GlobalPosition: 7,
            OccurredAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Operation: StateOperation.Set,
            Status: StateStatus.Ready,
            ValueType: typeof(string).FullName!,
            SchemaVersion: 1,
            Payload: "value"u8.ToArray(),
            FreshUntil: null,
            ServeUntil: null,
            Source: "test",
            CorrelationId: null,
            CausationId: null,
            Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Error: null);
        string document = JsonSerializer.Serialize(legacy, json);

        // Sanity-check the fixture itself: it must NOT end with the new suffix, or it is not
        // actually exercising the old order.
        Assert.DoesNotContain("\"globalPosition\":7}", document, StringComparison.Ordinal);

        IDatabase database = connection.GetDatabase();
        await database.SortedSetAddAsync($"statesman:{name}:changes", document, 7);

        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
        {
            changes.Add(envelope);
        }

        StateChangeEnvelope only = Assert.Single(changes);
        Assert.Equal(7, only.Record.GlobalPosition);
        Assert.Equal(1, only.Record.Revision);
        Assert.Equal("app", only.Record.Address.Root);
        Assert.Equal("script/legacy", only.Record.Address.Path.Value);
        Assert.Equal(StateOperation.Set, only.Record.Operation);
        Assert.Equal("value"u8.ToArray(), only.Record.Payload);
    }

    // The pre-Phase-8 property order of RedisRecord, as a positional record so declaration order
    // is the serialized order. Kept in the test project on purpose: it is a fixture describing
    // data that already exists in deployed Redis instances, not a type the provider should carry.
    private sealed record LegacyRedisRecord(
        string Root,
        string Path,
        string Partition,
        long Revision,
        long GlobalPosition,
        DateTimeOffset OccurredAt,
        StateOperation Operation,
        StateStatus Status,
        string ValueType,
        int SchemaVersion,
        byte[]? Payload,
        DateTimeOffset? FreshUntil,
        DateTimeOffset? ServeUntil,
        string Source,
        string? CorrelationId,
        string? CausationId,
        IReadOnlyDictionary<string, string> Metadata,
        StateError? Error);

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
