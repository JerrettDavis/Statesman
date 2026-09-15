using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

public sealed class RedisKeyLayoutTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private const string SkipReason =
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    private const string LegacyOnlyReason =
        "STATESMAN_TEST_REDIS_KEY_LAYOUT selects SingleSlot; this fact writes Legacy keys, which a "
        + "cluster refuses as CROSSSLOT. The standalone redis-tests job is where it runs.";

    [Fact]
    public async Task Legacy_writes_the_exact_key_names_Statesman_has_always_written()
    {
        // The byte-identity fact. Every key below is spelled here the way it was spelled before
        // RedisKeyLayout existed, so an accidental change of the default -- or of any one builder --
        // fails here rather than in a consumer's production data.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);
        Assert.SkipUnless(RedisTestLayout.Layout == RedisKeyLayout.Legacy, LegacyOnlyReason);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"layout-legacy-{Guid.NewGuid():N}";
        var store = new RedisStateLedgerStore(
            name,
            connection,
            new RedisStateLedgerStoreOptions { KeyLayout = RedisKeyLayout.Legacy });
        var address = new StateAddress("app", "layout/item", StatePartition.Default);

        Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"))).Succeeded);
        string stream = $"statesman:{name}:stream:{Hash(address)}";

        IDatabase database = connection.GetDatabase();
        Assert.True(await database.KeyExistsAsync($"{stream}:head"));
        Assert.True(await database.KeyExistsAsync($"{stream}:revision"));
        Assert.True(await database.KeyExistsAsync($"{stream}:history"));
        Assert.True(await database.KeyExistsAsync($"statesman:{name}:changes"));
        Assert.True(await database.KeyExistsAsync($"statesman:{name}:partitions"));
        Assert.True(await database.KeyExistsAsync($"statesman:{name}:global-position"));

        IStateLease? lease = await store.AcquireAsync("worker", TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);
        Assert.True(await database.KeyExistsAsync($"statesman:{name}:lease:worker"));
        await lease!.DisposeAsync();

        Assert.Equal(RedisKeyLayout.Legacy, new RedisStateLedgerStoreOptions().KeyLayout);
    }

    [Fact]
    public async Task SingleSlot_puts_every_key_of_one_store_in_one_hash_slot()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        string[] tagged = await WrittenKeysAsync(RedisKeyLayout.SingleSlot, $"layout-tagged-{Guid.NewGuid():N}");

        Assert.All(tagged, key => Assert.StartsWith("{statesman:layout-tagged-", key, StringComparison.Ordinal));
        Assert.Single(tagged.Select(HashSlot).Distinct());
    }

    [Fact]
    public async Task Legacy_spreads_one_stores_keys_across_more_than_one_hash_slot()
    {
        // The other half of the pair, and the reason the option exists at all. Without it a store's
        // six-key append script and its multi-address capture are CROSSSLOT on a cluster. If this
        // ever reports one slot, the fact above has stopped discriminating.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);
        Assert.SkipUnless(RedisTestLayout.Layout == RedisKeyLayout.Legacy, LegacyOnlyReason);

        string[] plain = await WrittenKeysAsync(RedisKeyLayout.Legacy, $"layout-plain-{Guid.NewGuid():N}");

        Assert.All(plain, key => Assert.StartsWith("statesman:layout-plain-", key, StringComparison.Ordinal));
        Assert.True(plain.Select(HashSlot).Distinct().Count() > 1);
    }

    private static async Task<string[]> WrittenKeysAsync(RedisKeyLayout layout, string name)
    {
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        var store = new RedisStateLedgerStore(
            name,
            connection,
            new RedisStateLedgerStoreOptions { KeyLayout = layout });

        // Two addresses, so the per-address stream keys differ from each other as well as from the
        // three per-store keys; one address would understate the spread Legacy produces.
        foreach (string path in new[] { "layout/a", "layout/b" })
        {
            var address = new StateAddress("app", path, StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"))).Succeeded);
        }

        IStateLease? lease = await store.AcquireAsync("worker", TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);

        IServer server = connection.GetServer(connection.GetEndPoints()[0]);
        string[] keys = server.Keys(pattern: $"*{name}*")
            .Select(key => key.ToString())
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        // Three per-address keys for each of two addresses, three per-store keys, and one lease.
        Assert.Equal(10, keys.Length);
        return keys;
    }

    private static string Hash(StateAddress address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Canonical))).ToLowerInvariant();

    /// <summary>
    /// Redis Cluster's own slot function: CRC16/XMODEM of the whole key, or of the substring
    /// between the first <c>{</c> and the first non-empty <c>}</c> after it, modulo 16384.
    /// Reimplemented here because StackExchange.Redis exposes no public equivalent and because a
    /// standalone server, which is what this suite usually runs against, refuses CLUSTER KEYSLOT.
    /// </summary>
    private static int HashSlot(string key)
    {
        int open = key.IndexOf('{', StringComparison.Ordinal);
        if (open >= 0)
        {
            int close = key.IndexOf('}', open + 1);
            if (close > open + 1)
            {
                key = key.Substring(open + 1, close - open - 1);
            }
        }

        ushort crc = 0;
        foreach (byte value in Encoding.UTF8.GetBytes(key))
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc % 16384;
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
}
