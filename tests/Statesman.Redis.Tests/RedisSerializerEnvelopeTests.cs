using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

public sealed class RedisSerializerEnvelopeTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private const string SkipReason =
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    private static readonly StateEnvelope Envelope = new()
    {
        ContentType = "application/json",
        SerializerId = "statesman.json/v1",
        Fingerprint = "fingerprint-redis",
    };

    [Fact]
    public async Task An_append_carrying_a_populated_envelope_keeps_globalPosition_last()
    {
        // The ordering fact, and the reason it has to write a POPULATED envelope. RedisRecord's
        // nullable members are omitted while null, so an envelope member declared after
        // GlobalPosition is invisible to every other test in this repository and throws only here,
        // on the first write that actually carries one. Measured with the member moved after
        // GlobalPosition: every other Redis fact still passes and this one throws
        // InvalidOperationException from PositionPrefix.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"envelope-{Guid.NewGuid():N}";
        await using var store = new RedisStateLedgerStore(name, connection, RedisTestLayout.Options());
        var address = new StateAddress("app", "envelope/item", StatePartition.Default);

        Assert.True((await store.AppendAsync(
            address,
            StateWriteCondition.Absent,
            Commit("one", Envelope),
            TestContext.Current.CancellationToken)).Succeeded);

        IDatabase database = connection.GetDatabase();
        string stream = $"{RedisTestLayout.Scope(name)}:stream:{Hash(address)}";
        string? stored = await database.StringGetAsync($"{stream}:head");
        Assert.NotNull(stored);

        // The two halves of the trap: the envelope is in the document, AND globalPosition is still
        // the last property. Asserting only the first would pass with the member in the wrong place
        // on any write path that does not go through the append script.
        Assert.Contains("\"envelope\":{", stored, StringComparison.Ordinal);
        Assert.EndsWith("\"globalPosition\":1}", stored, StringComparison.Ordinal);

        StateRecord? head = await store.ReadLatestAsync(address, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Equal(Envelope, head.Envelope);
    }

    [Fact]
    public async Task An_append_without_an_envelope_writes_the_member_the_previous_release_wrote()
    {
        // Byte identity for the un-enveloped case, which is what makes this feature free of any data
        // migration on Redis: member identity on this provider IS the serialized bytes across two
        // keys, so a member whose bytes moved would strand every prune and every member-exact import
        // against records written before this release.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"envelope-none-{Guid.NewGuid():N}";
        await using var store = new RedisStateLedgerStore(name, connection, RedisTestLayout.Options());
        var address = new StateAddress("app", "envelope/none", StatePartition.Default);

        Assert.True((await store.AppendAsync(
            address,
            StateWriteCondition.Absent,
            Commit("one", envelope: null),
            TestContext.Current.CancellationToken)).Succeeded);

        IDatabase database = connection.GetDatabase();
        string stream = $"{RedisTestLayout.Scope(name)}:stream:{Hash(address)}";
        string? head = await database.StringGetAsync($"{stream}:head");
        Assert.NotNull(head);
        Assert.DoesNotContain("\"envelope\":", head, StringComparison.Ordinal);

        // The same bytes in the history sorted set, which is the other key member identity spans.
        SortedSetEntry[] history = await database.SortedSetRangeByRankWithScoresAsync($"{stream}:history");
        SortedSetEntry entry = Assert.Single(history);
        Assert.Equal(head, (string?)entry.Element);
    }

    private static string Hash(StateAddress address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Canonical))).ToLowerInvariant();

    private static StateCommit Commit(string value, StateEnvelope? envelope) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Envelope = envelope,
        Source = "test",
    };
}
