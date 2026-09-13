using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// What <c>statesman.load.source.duration</c> records: one measurement per declared source, with that
/// source's exact elapsed time and its name in the fifth tag. In the serialised meter collection
/// because it reads instruments a concurrently running test could also write to.
/// </summary>
[Collection(nameof(MaintenanceFailureMeterCollection))]
public sealed class StateLoadTelemetryTests
{
    private static readonly StateKey<int> Timed = StateKey.Define<int>("load-telemetry/timed");

    [Fact]
    public async Task Each_source_records_its_exact_elapsed_time_under_its_own_name()
    {
        var measured = new List<(string Source, double Milliseconds)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter) &&
                instrument.Name == "statesman.load.source.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string source = tags.ToArray()
                .First(tag => tag.Key == "statesman.source").Value?.ToString() ?? string.Empty;
            lock (measured)
            {
                measured.Add((source, value));
            }
        });
        listener.Start();

        await using StatesmanTestHarness harness = Create();
        _ = await harness.Runtime.State(Timed).RefreshAsync();

        lock (measured)
        {
            Assert.Equal(new[] { ("slow", 250d), ("fast", 100d) }, measured);
        }
    }

    [Fact]
    public async Task A_faulted_source_still_records_the_time_it_spent_before_throwing()
    {
        // The source an operator most wants a duration for is the one that timed out.
        var measured = new List<(string Source, double Milliseconds)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter) &&
                instrument.Name == "statesman.load.source.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string source = tags.ToArray()
                .First(tag => tag.Key == "statesman.source").Value?.ToString() ?? string.Empty;
            lock (measured)
            {
                measured.Add((source, value));
            }
        });
        listener.Start();

        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-telemetry-partial")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("broken", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(400));
                        throw new TimeoutException("upstream timed out");
                    })
                    .Into((current, _, _) => current)
                    .BestEffort()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);

        _ = await harness.Runtime.State(Timed).RefreshAsync();

        lock (measured)
        {
            Assert.Equal(new[] { ("broken", 400d) }, measured);
        }
    }

    private static StatesmanTestHarness Create()
    {
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-telemetry")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("slow", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(250));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("fast", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(100));
                        return ValueTask.FromResult(2);
                    })
                    .Into((_, value, _) => value)))
            .Build();
        return StatesmanTestHarness.Create(declaration, time: clock);
    }
}
