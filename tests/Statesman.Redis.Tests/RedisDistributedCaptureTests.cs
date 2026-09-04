using StackExchange.Redis;

namespace Statesman.Redis.Tests;

public sealed class RedisDistributedCaptureTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    [Fact]
    public void RedisStateLedgerStore_reports_distributed_capture_capability()
    {
        Assert.True(typeof(RedisStateLedgerStore).GetInterfaces().Contains(typeof(IDistributedCapture)));
    }

    [Theory]
    [InlineData(StateCaptureConsistency.ProcessLocal)]
    [InlineData((StateCaptureConsistency)99)]
    public async Task CaptureAsync_rejects_a_consistency_level_this_store_does_not_back(
        StateCaptureConsistency consistency)
    {
        await using ConnectionMultiplexer connection = await OfflineConnectionAsync();
        var store = new RedisStateLedgerStore("capture-guard", connection);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.CaptureAsync(new[] { new StateAddress("app", "capture/a", StatePartition.Default) }, consistency));
    }

    [Fact]
    public async Task CaptureAsync_returns_an_empty_result_for_an_empty_address_set()
    {
        await using ConnectionMultiplexer connection = await OfflineConnectionAsync();
        var store = new RedisStateLedgerStore("capture-empty", connection);

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await store.CaptureAsync(
            Array.Empty<StateAddress>(), StateCaptureConsistency.SnapshotDistributed);

        Assert.Empty(captured);
    }

    // Both guarded paths above return before any command is issued, so they need a store instance
    // but not a reachable server. AbortOnConnectFail=false yields a multiplexer that never connects,
    // which keeps these two running in CI alongside the live-Redis tests rather than skipping with them.
    private static Task<ConnectionMultiplexer> OfflineConnectionAsync() =>
        ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { "localhost", 1 } },
            AbortOnConnectFail = false,
            ConnectRetry = 0,
            ConnectTimeout = 50,
        });

    [Theory]
    [InlineData(StateCaptureConsistency.ReadCommittedDistributed)]
    [InlineData(StateCaptureConsistency.SnapshotDistributed)]
    public async Task CaptureAsync_returns_the_current_record_per_address_including_absent_ones(
        StateCaptureConsistency consistency)
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore($"capture-test-{Guid.NewGuid():N}", connection);
        var addressA = new StateAddress("app", "capture/a", StatePartition.Default);
        var addressB = new StateAddress("app", "capture/b", StatePartition.Default);
        var addressC = new StateAddress("app", "capture/absent", StatePartition.Default);
        StateAppendResult a = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult b = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await store.CaptureAsync(
            new[] { addressA, addressB, addressC }, consistency);

        Assert.Equal(3, captured.Count);
        Assert.Equal(a.Record!.GlobalPosition, captured[addressA]!.GlobalPosition);
        Assert.Equal(b.Record!.GlobalPosition, captured[addressB]!.GlobalPosition);
        Assert.Null(captured[addressC]);
    }

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
