using System.Diagnostics;
using System.Text;

namespace Statesman.Tests;

/// <summary>
/// The committed baseline for the in-memory change feed's full drain, which the ROADMAP 0.3 Phase 11
/// final review measured at 41 ms for 200,000 records and deferred.
/// </summary>
/// <remarks>
/// <para>
/// Gated on <c>STATESMAN_MEASURE_INMEMORY_DRAIN=1</c> and skipped otherwise: the largest case
/// imports 200,000 records, which has no place in an ordinary test run or in CI.
/// </para>
/// <para>
/// Nothing here asserts a wall-clock figure. A timing threshold on a shared runner is a flake, not a
/// gate; the numbers go through <see cref="ITestOutputHelper"/> and are read by a human. Pass
/// <c>--show-stdout all --show-test-results all</c> to see them.
/// </para>
/// </remarks>
public sealed class InMemoryChangeFeedDrainMeasurement(ITestOutputHelper output)
{
    private const string Gate = "STATESMAN_MEASURE_INMEMORY_DRAIN";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(Gate), "1", StringComparison.Ordinal);

    private static string SkipReason =>
        $"{Gate} is not set to 1; skipping the in-memory change-feed drain measurement.";

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public async Task Unbounded_drain(int records)
    {
        // The shape the positional indexer costs O(N log N): one ReadAsync from the start with no
        // Take, walking every entry. This is what D4 changes.
        Assert.SkipUnless(Enabled, SkipReason);

        await using var store = new InMemoryStateLedgerStore("measure");
        await SeedAsync(store, records);

        var stopwatch = Stopwatch.StartNew();
        int yielded = 0;
        await foreach (StateChangeEnvelope _ in store.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            yielded++;
        }

        stopwatch.Stop();

        Assert.Equal(records, yielded);
        output.WriteLine(FormattableString.Invariant(
            $"unbounded-drain records={records} elapsed={stopwatch.Elapsed.TotalMilliseconds:F2}ms"));
    }

    [Theory]
    [InlineData(200_000)]
    public async Task Paged_drain_from_a_deep_cursor(int records)
    {
        // The shape the indexer is GOOD at, and the one D4 must not regress: a consumer resuming
        // deep in a long feed and taking one small page. Seeking there with the set's own enumerator
        // would walk every entry below the cursor.
        Assert.SkipUnless(Enabled, SkipReason);

        await using var store = new InMemoryStateLedgerStore("measure");
        await SeedAsync(store, records);

        var cursor = new StateChangeCursor(records - 200);
        var options = new StateChangeReadOptions { Take = 100 };
        var stopwatch = Stopwatch.StartNew();
        int yielded = 0;
        for (int repeat = 0; repeat < 100; repeat++)
        {
            yielded = 0;
            await foreach (StateChangeEnvelope _ in store.ReadAsync(cursor, options))
            {
                yielded++;
            }
        }

        stopwatch.Stop();

        Assert.Equal(100, yielded);
        output.WriteLine(FormattableString.Invariant(
            $"deep-page records={records} pages=100 elapsed={stopwatch.Elapsed.TotalMilliseconds:F2}ms"));
    }

    private static async Task SeedAsync(InMemoryStateLedgerStore store, int records)
    {
        // Imported at explicit positions rather than appended, so the feed has exactly `records`
        // entries at positions 1..records and the seed does not measure the append path.
        for (int index = 1; index <= records; index++)
        {
            await store.ImportAsync(new StateRecord
            {
                Address = new StateAddress("app", $"measure/{index % 1_000}", StatePartition.Default),
                Revision = (index / 1_000) + 1,
                GlobalPosition = index,
                OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Operation = StateOperation.Imported,
                Status = StateStatus.Ready,
                ValueType = typeof(string).FullName!,
                SchemaVersion = 1,
                Payload = Encoding.UTF8.GetBytes("v"),
                Source = "test",
            });
        }
    }
}
