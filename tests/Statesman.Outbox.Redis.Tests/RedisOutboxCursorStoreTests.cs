using StackExchange.Redis;
using Statesman.Outbox.Tests;

namespace Statesman.Outbox.Redis.Tests;

public sealed class RedisOutboxCursorStoreTests : OutboxCursorStoreConformanceTests, IDisposable
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private readonly string _keyPrefix = $"outbox-cursor-test-{Guid.NewGuid():N}";
    private IConnectionMultiplexer? _connection;

    protected override void SkipIfUnavailable() =>
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(ConnectionString),
            "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.");

    protected override IOutboxCursorStore CreateStore()
    {
        _connection ??= ConnectionMultiplexer.Connect(ConnectionString!);
        return new RedisOutboxCursorStore(_connection, new RedisOutboxCursorStoreOptions { KeyPrefix = _keyPrefix });
    }

    public void Dispose() => _connection?.Dispose();
}
