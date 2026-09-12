using System.Diagnostics;
using System.Globalization;

namespace Statesman.Outbox.Tests;

/// <summary>
/// The committed baseline for one <see cref="FileSystemOutboxCursorStore.WriteAsync"/> call.
/// <c>WriteAsync</c> makes three durability calls for one small write — <c>FileOptions.WriteThrough</c>
/// on the stream, <c>FlushAsync</c>, and <c>Flush(flushToDisk: true)</c> — and Phase 7 parked the
/// question of whether that is two more than it needs. This class is the artifact that answers it.
/// </summary>
/// <remarks>
/// <para>
/// Gated on <c>STATESMAN_MEASURE_OUTBOX_CURSOR_WRITE=1</c> and skips otherwise: 200 real fsyncs
/// against a real disk has no place in an ordinary test run or in CI.
/// </para>
/// <para>
/// Nothing here asserts a wall-clock figure. A timing threshold on a shared runner is a flake, not a
/// gate; the numbers are reported through <see cref="ITestOutputHelper"/> and read by a human. Run it
/// in Debug AND Release before changing the write path — a perf change that measures slower is a
/// regression (ROADMAP 0.3 Phase 12, D4, reverted byte-identical).
/// </para>
/// <para>
/// The xUnit v3 console runner swallows <see cref="ITestOutputHelper"/> output by default; pass
/// <c>--show-stdout all --show-test-results all</c> to see these lines.
/// </para>
/// </remarks>
public sealed class FileSystemOutboxCursorWriteMeasurement(ITestOutputHelper output)
{
    private const string Gate = "STATESMAN_MEASURE_OUTBOX_CURSOR_WRITE";
    private const int Writes = 200;

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(Gate), "1", StringComparison.Ordinal);

    private static string SkipReason =>
        $"{Gate} is not set to 1; skipping the filesystem outbox cursor write measurement.";

    [Fact]
    public async Task Two_hundred_monotonic_writes()
    {
        Assert.SkipUnless(Enabled, SkipReason);

        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemOutboxCursorStore(directory);

            // One warm write, so the file exists and the directory is in the OS cache: the figure
            // being measured is a steady-state cursor advance, not a first-write cost.
            await store.WriteAsync("measure", new StateChangeCursor(1));

            var elapsed = new List<double>(Writes);
            for (int index = 2; index <= Writes + 1; index++)
            {
                long start = Stopwatch.GetTimestamp();
                await store.WriteAsync("measure", new StateChangeCursor(index));
                elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            elapsed.Sort();
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"writes={Writes} median={elapsed[elapsed.Count / 2]:F3}ms p95={elapsed[(int)(elapsed.Count * 0.95)]:F3}ms total={elapsed.Sum():F1}ms"));

            // The cursor still round-trips, so the measurement cannot pass on a write path that
            // stopped writing.
            StateChangeCursor? stored = await store.ReadAsync("measure");
            Assert.Equal(Writes + 1, stored!.Value.Position);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
