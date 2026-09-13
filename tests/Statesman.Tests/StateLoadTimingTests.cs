using System.Globalization;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// The three bounded timing keys a completed load stamps on the record, and the fact that per-source
/// timing never lands there. Timing comes from the runtime's own <see cref="TimeProvider"/>, so a test
/// asserts exact elapsed values rather than real-time bounds: <c>ManualTimeProvider</c> overrides
/// <c>GetTimestamp</c> and <c>TimestampFrequency</c>, which makes
/// <see cref="TimeProvider.GetElapsedTime(long)"/> measure exact virtual time.
/// </summary>
public sealed class StateLoadTimingTests
{
    private static readonly StateKey<int> Timed = StateKey.Define<int>("load-timing/timed");
    private static readonly StateKey<int> Seeded = StateKey.Define<int>("load-timing/seeded");

    [Fact]
    public async Task A_completed_load_stamps_the_whole_load_duration_in_milliseconds()
    {
        await using StatesmanTestHarness harness = CreateSingleSource(TimeSpan.FromMilliseconds(250));

        IStateSnapshot<int> loaded = await harness.Runtime.State(Timed).RefreshAsync();

        Assert.Equal("250", loaded.Metadata["statesman.load.duration.ms"]);
        Assert.Equal("complete", loaded.Metadata["statesman.load.completeness"]);
    }

    [Fact]
    public async Task The_started_and_completed_keys_round_trip_and_differ_by_exactly_the_duration()
    {
        await using StatesmanTestHarness harness = CreateSingleSource(TimeSpan.FromMilliseconds(250));

        IStateSnapshot<int> loaded = await harness.Runtime.State(Timed).RefreshAsync();

        var started = DateTimeOffset.Parse(
            loaded.Metadata["statesman.load.started"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var completed = DateTimeOffset.Parse(
            loaded.Metadata["statesman.load.completed"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.Equal(TimeSpan.FromMilliseconds(250), completed - started);
    }

    [Fact]
    public async Task A_declaration_with_no_sources_still_stamps_the_timing_keys()
    {
        // The early return for a seed-only state is a second exit from the loader, and a shape that
        // differed between the two exits is exactly what an operator reading a stored record would
        // trip over.
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-timing-seeded")
            .State(Seeded, state => state.Initial(7))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);

        IStateSnapshot<int> loaded = await harness.Runtime.State(Seeded).RefreshAsync();

        Assert.Equal("seeded", loaded.Metadata["statesman.load.completeness"]);
        Assert.Equal("0", loaded.Metadata["statesman.load.duration.ms"]);
        Assert.True(loaded.Metadata.ContainsKey("statesman.load.started"));
        Assert.True(loaded.Metadata.ContainsKey("statesman.load.completed"));
    }

    [Fact]
    public async Task No_per_source_duration_key_is_ever_written_to_the_record()
    {
        // The bounded-ness fact. Every provider persists this dictionary on the head record and on
        // every history record, so a per-source key would scale permanent storage with the number of
        // declared sources. Addendum decision 68.
        await using StatesmanTestHarness harness = CreateTwoSources(
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(100));

        IStateSnapshot<int> loaded = await harness.Runtime.State(Timed).RefreshAsync();

        string[] perSourceKeys = [.. loaded.Metadata.Keys
            .Where(key => key.StartsWith("statesman.source.", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(
            new[] { "statesman.source.fast.status", "statesman.source.slow.status" },
            perSourceKeys);
    }

    [Fact]
    public async Task A_faulted_source_does_not_stop_the_timing_keys_from_landing()
    {
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-timing-partial")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(250));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("broken", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(100));
                        throw new InvalidOperationException("broken source");
                    })
                    .Into((current, _, _) => current)
                    .BestEffort()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);

        IStateSnapshot<int> loaded = await harness.Runtime.State(Timed).RefreshAsync();

        Assert.Equal("partial", loaded.Metadata["statesman.load.completeness"]);
        Assert.Equal("faulted", loaded.Metadata["statesman.source.broken.status"]);
        Assert.Equal("350", loaded.Metadata["statesman.load.duration.ms"]);
    }

    private static StatesmanTestHarness CreateSingleSource(TimeSpan takes)
    {
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-timing")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(takes);
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)))
            .Build();
        return StatesmanTestHarness.Create(declaration, time: clock);
    }

    private static StatesmanTestHarness CreateTwoSources(TimeSpan slow, TimeSpan fast)
    {
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-timing-two")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(slow);
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("fast", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(fast);
                        return ValueTask.FromResult(2);
                    })
                    .Into((_, value, _) => value)))
            .Build();
        return StatesmanTestHarness.Create(declaration, time: clock);
    }
}
