using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;
using StackExchange.Redis;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The one place every conformance suite constructs a provider's store. Six suites times five
/// providers is thirty constructions; inlining them would mean six copies of each provider's setup,
/// and the filesystem cleanup and the Redis connection handover are exactly the parts that rot when
/// copied. Each factory returns a <see cref="ConformanceStore"/> with default options, or
/// <see langword="null"/> when that provider's infrastructure is not available here, which is the
/// signal every suite turns into an <c>Assert.SkipUnless</c>.
/// </summary>
public static class ConformanceProviders
{
    /// <summary>Shown when a Redis-backed suite is skipped.</summary>
    public const string RedisSkipReason =
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    private static string? RedisConnectionString =>
        Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    /// <summary>The in-memory provider.</summary>
    public static ValueTask<ConformanceStore?> InMemoryAsync(TimeProvider clock)
    {
        var store = new InMemoryStateLedgerStore("memory", clock);
        return ValueTask.FromResult<ConformanceStore?>(new ConformanceStore
        {
            Store = store,
            Feed = store,
        });
    }

    /// <summary>
    /// The filesystem provider, with default options — including <c>FlushToDisk = true</c>. Its
    /// change log is append-only, so its feed repair is a maintenance step rather than something a
    /// write does: <see cref="ConformanceStore.Maintain"/> is set to <c>CompactChangeLogAsync</c>.
    /// </summary>
    public static ValueTask<ConformanceStore?> FileSystemAsync(TimeProvider clock)
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemStateLedgerStore(
            "feed",
            new FileSystemStateLedgerStoreOptions { RootDirectory = directory },
            clock);

        return ValueTask.FromResult<ConformanceStore?>(new ConformanceStore
        {
            Store = store,
            Feed = store,
            Maintain = async () => _ = await store.CompactChangeLogAsync(),
            Cleanup = () =>
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return ValueTask.CompletedTask;
            },
        });
    }

    /// <summary>
    /// The tiered provider over an in-memory hot replica and an in-memory cold authority. Append and
    /// read both delegate straight to cold, so this is the fixture every suite wants except
    /// distributed capture, which needs a capture-capable cold tier — see
    /// <see cref="TieredOverEntityFrameworkAsync"/>.
    /// </summary>
    public static ValueTask<ConformanceStore?> TieredOverInMemoryAsync(TimeProvider clock)
    {
        var cold = new InMemoryStateLedgerStore("tiered-conformance-cold", clock);
        var hot = new InMemoryStateLedgerStore("tiered-conformance-hot");
        var store = new TieredStateLedgerStore("tiered-conformance", hot, cold, ownsStores: true);
        return ValueTask.FromResult<ConformanceStore?>(new ConformanceStore
        {
            Store = store,
            Feed = store,
        });
    }

    /// <summary>A live Redis when <c>STATESMAN_TEST_REDIS</c> is set, and null when it is not.</summary>
    public static async ValueTask<ConformanceStore?> RedisAsync(TimeProvider clock)
    {
        string? connectionString = RedisConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var store = new RedisStateLedgerStore(
            $"conformance-{Guid.NewGuid():N}",
            connection,
            new RedisStateLedgerStoreOptions { OwnsConnection = true },
            clock);

        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Leases = store,
        };
    }

    /// <summary>
    /// The Entity Framework Core provider. <paramref name="concurrency"/> is load-bearing: the
    /// change-feed suite's in-flight test needs two concurrent transactions, which one
    /// Microsoft.Data.Sqlite connection object cannot host, while every sequential suite is fine on
    /// the shared single-connection fixture.
    /// </summary>
    public static async ValueTask<ConformanceStore?> EntityFrameworkAsync(
        TimeProvider clock,
        EntityFrameworkTestConcurrency concurrency = EntityFrameworkTestConcurrency.SingleConnection)
    {
        EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(concurrency);
        TestDbContextFactory<ConformanceLedgerContext> factory =
            await database.CreateFactoryAsync<ConformanceLedgerContext>(
                options => new ConformanceLedgerContext(options));

        var store = new EntityFrameworkStateLedgerStore<ConformanceLedgerContext>("database", factory, clock);
        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Leases = store,
            Cleanup = () => database.DisposeAsync(),
        };
    }

    /// <summary>
    /// The tiered provider over an Entity Framework Core cold authority. Only the capture suite needs
    /// it: <c>CaptureAsync</c> always delegates to cold, and the in-memory provider has no
    /// <see cref="IDistributedCapture"/>, so tiered over in-memory throws
    /// <see cref="NotSupportedException"/> naming the cold tier instead of exercising the delegation.
    /// Entity Framework Core rather than Redis, so this runs with no live infrastructure.
    /// </summary>
    public static async ValueTask<ConformanceStore?> TieredOverEntityFrameworkAsync(TimeProvider clock)
    {
        EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<ConformanceLedgerContext> factory =
            await database.CreateFactoryAsync<ConformanceLedgerContext>(
                options => new ConformanceLedgerContext(options));

        var cold = new EntityFrameworkStateLedgerStore<ConformanceLedgerContext>("tiered-cold", factory, clock);
        var hot = new InMemoryStateLedgerStore("tiered-hot");
        var store = new TieredStateLedgerStore("tiered-over-database", hot, cold, ownsStores: true);
        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Cleanup = () => database.DisposeAsync(),
        };
    }

    /// <summary>The ledger context every Entity Framework Core conformance fixture uses.</summary>
    public sealed class ConformanceLedgerContext : StatesmanLedgerDbContext
    {
        /// <summary>Creates the context.</summary>
        /// <param name="options">Options from the test database.</param>
        public ConformanceLedgerContext(DbContextOptions<ConformanceLedgerContext> options)
            : base(options)
        {
        }
    }
}
