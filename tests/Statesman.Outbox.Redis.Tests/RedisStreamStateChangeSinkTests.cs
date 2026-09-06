using System.Text;
using StackExchange.Redis;

namespace Statesman.Outbox.Redis.Tests;

public sealed class RedisStreamStateChangeSinkTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private static void SkipIfUnavailable() =>
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

    private static StateChangeMessage Message(long position, long revision = 1, byte[]? payload = null) =>
        StateChangeMessage.FromRecord(
            new StateRecord
            {
                Address = new StateAddress("app", "orders/basket", StatePartition.Default),
                Revision = revision,
                GlobalPosition = position,
                OccurredAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
                Operation = StateOperation.Set,
                Status = StateStatus.Ready,
                ValueType = "Contoso.Basket",
                SchemaVersion = 1,
                Payload = payload,
                Source = "test",
                CorrelationId = "corr-1",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["region"] = "eu" },
            },
            store: "primary",
            fingerprint: "fp-abc");

    private static RedisStreamStateChangeSinkOptions Options() =>
        new() { KeyPrefix = $"outbox-sink-test-{Guid.NewGuid():N}" };

    [Fact]
    public async Task Publishes_one_entry_per_message_keyed_by_global_position()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        RedisStreamStateChangeSinkOptions options = Options();
        await using var sink = new RedisStreamStateChangeSink(connection, options);

        await sink.PublishAsync([Message(1), Message(2, 2), Message(3, 3)]);

        StreamEntry[] entries = await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+");
        Assert.Equal(new[] { "1-0", "2-0", "3-0" }, entries.Select(entry => entry.Id.ToString()).ToArray());
    }

    [Fact]
    public async Task Every_declared_field_is_present_on_the_entry()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        await using var sink = new RedisStreamStateChangeSink(connection, Options());

        await sink.PublishAsync([Message(1, payload: Encoding.UTF8.GetBytes("{\"a\":1}"))]);

        StreamEntry entry = Assert.Single(await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+"));
        Assert.Equal("statesman.state-change/v1", (string?)entry["format"]);
        Assert.Equal("primary/app::orders/basket::default#1", (string?)entry["messageId"]);
        Assert.Equal("primary", (string?)entry["store"]);
        Assert.Equal("app", (string?)entry["root"]);
        Assert.Equal("orders/basket", (string?)entry["path"]);
        Assert.Equal("default", (string?)entry["partition"]);
        Assert.Equal("app::orders/basket::default", (string?)entry["address"]);
        Assert.Equal("1", (string?)entry["revision"]);
        Assert.Equal("1", (string?)entry["globalPosition"]);
        Assert.Equal("Set", (string?)entry["operation"]);
        Assert.Equal("Ready", (string?)entry["status"]);
        Assert.Equal("Contoso.Basket", (string?)entry["valueType"]);
        Assert.Equal("1", (string?)entry["schemaVersion"]);
        Assert.Equal("test", (string?)entry["source"]);
        Assert.Equal("fp-abc", (string?)entry["fingerprint"]);
        Assert.Equal("application/json", (string?)entry["contentType"]);
        Assert.Equal("corr-1", (string?)entry["correlationId"]);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((byte[])entry["payload"]!));
        Assert.Contains("\"region\":\"eu\"", (string)entry["metadata"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_without_a_payload_omits_the_payload_and_content_type_fields()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        await using var sink = new RedisStreamStateChangeSink(connection, Options());

        await sink.PublishAsync([Message(1)]);

        StreamEntry entry = Assert.Single(await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+"));
        Assert.True(entry["payload"].IsNull);
        Assert.True(entry["contentType"].IsNull);
    }

    [Fact]
    public async Task Republishing_the_same_batch_adds_no_duplicate_entries()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        await using var sink = new RedisStreamStateChangeSink(connection, Options());
        StateChangeMessage[] batch = [Message(1), Message(2, 2)];

        await sink.PublishAsync(batch);
        await sink.PublishAsync(batch);

        StreamEntry[] entries = await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+");
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, sink.Deduplicated);
    }

    [Fact]
    public async Task A_partially_duplicate_batch_still_publishes_its_new_messages()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        await using var sink = new RedisStreamStateChangeSink(connection, Options());

        await sink.PublishAsync([Message(1)]);
        await sink.PublishAsync([Message(1), Message(2, 2)]);

        StreamEntry[] entries = await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+");
        Assert.Equal(new[] { "1-0", "2-0" }, entries.Select(entry => entry.Id.ToString()).ToArray());
        Assert.Equal(1, sink.Deduplicated);
    }

    [Fact]
    public async Task A_position_above_two_to_the_fifty_third_is_an_exact_entry_id()
    {
        SkipIfUnavailable();
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        await using var sink = new RedisStreamStateChangeSink(connection, Options());

        await sink.PublishAsync([Message(638000000000000000)]);

        StreamEntry entry = Assert.Single(await connection.GetDatabase().StreamRangeAsync(sink.StreamKey, "-", "+"));
        Assert.Equal("638000000000000000-0", entry.Id.ToString());
        Assert.Equal("638000000000000000", (string?)entry["globalPosition"]);
    }
}
