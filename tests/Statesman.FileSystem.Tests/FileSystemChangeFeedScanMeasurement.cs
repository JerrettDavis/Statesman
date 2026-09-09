using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// The committed baseline for the filesystem change-log scan. Phase 10 documented
/// "2.3 / 18.5 / 83.7 ms per scan at 5,000 / 50,000 / 200,000 change-log lines" as prose with no
/// artifact behind it; this class is that artifact, so Phase 11's index has a "before" to measure
/// against and any later change to this read path has a way to re-establish the figures.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement is gated on <c>STATESMAN_MEASURE_FEED_SCAN=1</c> and skips otherwise. The
/// largest case writes 200,000 change-log lines and the drain case makes 20,000 real appends against
/// a real disk, which is minutes of work and has no place in an ordinary test run or in CI.
/// </para>
/// <para>
/// Nothing here asserts a wall-clock figure. A timing threshold on a shared runner is a flake, not a
/// gate; the numbers are reported through <see cref="ITestOutputHelper"/> and read by a human, which
/// is what the Phase 10 fsync measurement actually did.
/// </para>
/// </remarks>
public sealed class FileSystemChangeFeedScanMeasurement(ITestOutputHelper output)
{
    private const string Gate = "STATESMAN_MEASURE_FEED_SCAN";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(Gate), "1", StringComparison.Ordinal);

    private static string SkipReason =>
        $"{Gate} is not set to 1; skipping the filesystem change-log scan measurement.";

    [Theory]
    [InlineData(5_000)]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public async Task Scan_at_the_tail(int lines)
    {
        // The steady-state shape: a caught-up consumer reads from the tail and gets nothing back,
        // having paid for whatever the provider has to parse to find that out. No history files are
        // written, so ReadAsync parses every line and dereferences none -- this measures the log
        // scan, which is the cost the index changes, and not history-file I/O.
        //
        // The first read is reported separately from the median of the next ten because that is the
        // whole point of an index: before it the two are the same number, and after it the first is
        // the one-off parse and the second is the incremental cost.
        Assert.SkipUnless(Enabled, SkipReason);

        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await WriteChangeLogAsync(directory, lines, addressCount: 1_000);

            // Pays the file's first-touch cost (NTFS metadata / AV first access on a brand-new
            // path) up front, outside anything that is timed, so the "first" ReadAsync below
            // measures the store's parse of the whole log and nothing else. This must go through
            // File.ReadAllBytesAsync directly and NOT through the store's own ReadAsync: reading
            // via the store would double as a warm-up call against whatever the read path caches,
            // which is exactly the first-vs-median signal this test exists to capture.
            _ = await File.ReadAllBytesAsync(Path.Combine(directory, "_changes.log"));

            await using var store = new FileSystemStateLedgerStore(
                "measure", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            var options = new StateChangeReadOptions { Take = 100 };
            var cursor = new StateChangeCursor(lines);

            double first = await TimeReadAsync(store, cursor, options);
            var subsequent = new List<double>();
            for (int index = 0; index < 10; index++)
            {
                subsequent.Add(await TimeReadAsync(store, cursor, options));
            }

            subsequent.Sort();
            double median = (subsequent[4] + subsequent[5]) / 2;

            output.WriteLine(FormattableString.Invariant(
                $"scan-at-tail lines={lines} first={first:F2}ms median-of-next-ten={median:F2}ms"));
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
    public async Task Backlog_drain()
    {
        // The shape Phase 10's paging actually made quadratic: an outbox draining a backlog reads
        // page after page from one store instance in one process, and each page re-parses the log.
        // FlushToDisk = false because this measures the read path -- 20,000 fsync'd appends would
        // spend all their time in the write path and swamp the figure being taken.
        Assert.SkipUnless(Enabled, SkipReason);

        const int addressCount = 100;
        const int totalRecords = 20_000;
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "measure",
                new FileSystemStateLedgerStoreOptions { RootDirectory = directory, FlushToDisk = false });

            StateAddress[] addresses = [.. Enumerable.Range(0, addressCount)
                .Select(index => new StateAddress("app", $"measure/{index}", StatePartition.Default))];

            var writeClock = Stopwatch.StartNew();
            for (int index = 0; index < totalRecords; index++)
            {
                long revision = index / addressCount;
                StateWriteCondition condition = revision == 0
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision);
                StateAppendResult result = await store.AppendAsync(
                    addresses[index % addressCount], condition, Commit($"v{revision}"));
                Assert.True(result.Succeeded);
            }

            writeClock.Stop();

            var drainClock = Stopwatch.StartNew();
            var options = new StateChangeReadOptions { Take = 100 };
            StateChangeCursor? cursor = null;
            int pages = 0;
            int records = 0;
            while (true)
            {
                int page = 0;
                StateChangeCursor? last = null;
                await foreach (StateChangeEnvelope envelope in store.ReadAsync(cursor, options))
                {
                    page++;
                    last = envelope.Cursor;
                }

                if (page == 0)
                {
                    break;
                }

                pages++;
                records += page;
                cursor = last;
            }

            drainClock.Stop();

            output.WriteLine(FormattableString.Invariant(
                $"backlog-drain records={records} pages={pages} write={writeClock.Elapsed.TotalMilliseconds:F0}ms drain={drainClock.Elapsed.TotalMilliseconds:F0}ms"));

            // The one thing worth asserting: the drain is complete. A measurement of a drain that
            // silently lost records would be a number about nothing.
            Assert.Equal(totalRecords, records);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<double> TimeReadAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from,
        StateChangeReadOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        int yielded = 0;
        await foreach (StateChangeEnvelope _ in feed.ReadAsync(from, options))
        {
            yielded++;
        }

        stopwatch.Stop();

        // yielded is asserted rather than discarded so that a future change which starts yielding
        // here shows up as a failure instead of quietly changing what is being timed.
        Assert.Equal(0, yielded);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task WriteChangeLogAsync(string directory, int lines, int addressCount)
    {
        // The five tab-separated fields AppendChangeFeedEntryUnsafeAsync writes, newline-terminated:
        // position, root, path, partition, revision. Written by hand so the log can be arbitrarily
        // long without the cost of arbitrarily many appends, and with NO history files behind it so
        // that every entry dangles and nothing is dereferenced.
        var builder = new StringBuilder();
        for (int index = 1; index <= lines; index++)
        {
            builder
                .Append(index.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append("app").Append('\t')
                .Append("measure/").Append((index % addressCount).ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(StatePartition.Default.Value).Append('\t')
                .Append(((index / addressCount) + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "_changes.log"), builder.ToString());
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
