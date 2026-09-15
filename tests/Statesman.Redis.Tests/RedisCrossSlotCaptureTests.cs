using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

/// <summary>
/// Pins <see cref="RedisStateLedgerStore"/>'s CROSSSLOT-to-<see cref="NotSupportedException"/>
/// translation against a real cluster. Final review, fix wave, finding C1: StackExchange.Redis
/// validates slots client-side before dispatch and raises <see cref="RedisCommandException"/> with
/// the message "Multi-key operations must involve a single slot ...", which carries no
/// <c>CROSSSLOT</c> token, so this translation was dead code until the fix wave widened
/// <c>IsCrossSlotFailure</c> by one operand. This fact constructs its store with
/// <see cref="RedisKeyLayout.Legacy"/> explicitly, regardless of
/// <see cref="RedisTestLayout.Layout"/>, because it is about the untagged layout on a cluster.
/// </summary>
public sealed class RedisCrossSlotCaptureTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private const string SkipReason =
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    private const string NotClusterReason =
        "STATESMAN_TEST_REDIS does not point at a cluster; this fact needs a real client-side "
        + "cross-slot rejection, which only a cluster produces. The redis-cluster job is where it runs.";

    [Fact]
    public async Task CaptureAsync_under_Legacy_on_a_cluster_translates_the_cross_slot_rejection()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        // AllowAdmin: CLUSTER INFO is categorized as an admin command by StackExchange.Redis's own
        // command map, and IServer.ExecuteAsync refuses it without this, regardless of what the
        // server itself would allow.
        ConfigurationOptions options = ConfigurationOptions.Parse(ConnectionString!);
        options.AllowAdmin = true;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        Assert.SkipUnless(await IsClusterAsync(connection), NotClusterReason);

        var store = new RedisStateLedgerStore(
            $"crossslot-capture-{Guid.NewGuid():N}",
            connection,
            new RedisStateLedgerStoreOptions { KeyLayout = RedisKeyLayout.Legacy });

        // 24 distinct addresses; 24 distinct stream-key hashes are certain to span slots on a
        // 16384-slot cluster. The addresses need not exist: the client-side slot check runs before
        // any read reaches a missing key.
        StateAddress[] addresses = Enumerable.Range(0, 24)
            .Select(i => new StateAddress("app", $"crossslot/{i}", StatePartition.Default))
            .ToArray();

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await store.CaptureAsync(addresses, StateCaptureConsistency.ReadCommittedDistributed));

        Assert.Contains("spanning multiple hash slots", exception.Message, StringComparison.Ordinal);
        Assert.IsType<RedisCommandException>(exception.InnerException);
    }

    /// <summary>
    /// Runs <c>CLUSTER INFO</c> and answers honestly: a standalone server raises
    /// <see cref="RedisServerException"/> with "This instance has cluster support disabled" rather
    /// than returning a benign default, which this treats as "not a cluster" instead of letting the
    /// exception escape and fail the fact for the wrong reason.
    /// </summary>
    /// <param name="connection">The connection to probe.</param>
    /// <returns><see langword="true"/> when the server reports <c>cluster_state:ok</c>.</returns>
    private static async Task<bool> IsClusterAsync(ConnectionMultiplexer connection)
    {
        IServer server = connection.GetServer(connection.GetEndPoints()[0]);
        try
        {
            RedisResult info = await server.ExecuteAsync("CLUSTER", "INFO");
            return (info.ToString() ?? string.Empty).Contains("cluster_state:ok", StringComparison.Ordinal);
        }
        catch (RedisServerException exception)
            when (exception.Message.Contains("cluster support disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }
}
