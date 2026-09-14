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

    [Fact]
    public async Task A_RequireAll_load_that_throws_still_records_every_source_it_measured()
    {
        // The gap ROADMAP 0.3 Phase 17 measured and parked: a load that throws outright records no
        // per-source timing on ANY channel, in the one mode an operator chooses precisely because
        // every source matters. The reports are already fully populated at the throw site; they now
        // travel on the exception's Data so the existing catch can record them.
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
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-telemetry-requireall")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("first", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(120));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("second", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(330));
                        throw new TimeoutException("upstream timed out");
                    })
                    .Into((current, _, _) => current)
                    .RequireAll()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);

        // The load throws, the handle turns it into a fault snapshot, and RefreshAsync returns that
        // snapshot rather than rethrowing -- which is why this is an assertion about the meter and not
        // about an exception.
        IStateSnapshot<int> snapshot = await harness.Runtime.State(Timed).RefreshAsync();
        Assert.Equal(StateStatus.Faulted, snapshot.Status);

        lock (measured)
        {
            Assert.Equal(new[] { ("first", 120d), ("second", 330d) }, measured);
        }
    }

    [Fact]
    public async Task A_RequireAll_load_in_parallel_that_throws_still_records_every_source_it_measured()
    {
        // I2 (final review, 2026-09-14): the sequential fact above pins throw site 4
        // (StateSourceExecution.Sequential); this pins throw site 2, the parallel branch. Before the
        // fix, sourceReports at that throw held only the FAULTED fetches -- the apply loop that would
        // normally write a ReadySource entry for a successful fetch never runs, because the throw
        // happens first -- so a source that fetched cleanly lost its measured duration entirely. "ok"
        // fetches successfully in 60 ms; "bad" fails after 90 ms.
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
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-telemetry-requireall-parallel")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("ok", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(60));
                        return ValueTask.FromResult(1);
                    })
                    .Into((_, value, _) => value)
                    .From<TimeProvider, int>("bad", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(90));
                        throw new TimeoutException("upstream timed out");
                    })
                    .Into((current, _, _) => current)
                    .InParallel()
                    .RequireAll()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);

        IStateSnapshot<int> snapshot = await harness.Runtime.State(Timed).RefreshAsync();
        Assert.Equal(StateStatus.Faulted, snapshot.Status);

        lock (measured)
        {
            Assert.Equal(new[] { ("bad", 90d), ("ok", 60d) }, measured);
        }
    }

    [Fact]
    public async Task A_throwing_load_still_records_no_report_and_the_fault_type_is_unchanged()
    {
        // Two facts decision 74 and decision 85 depend on, pinned together because they are the two
        // things this change deliberately did NOT do.
        var clock = new ManualTimeProvider();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("load-telemetry-noreport")
            .State(Timed, state => state
                .Initial(0)
                .Load(load => load
                    .From<TimeProvider, int>("only", (_, context, _) =>
                    {
                        ((ManualTimeProvider)context.TimeProvider).Advance(TimeSpan.FromMilliseconds(70));
                        throw new TimeoutException("upstream timed out");
                    })
                    .Into((current, _, _) => current)
                    .RequireAll()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, time: clock);
        Assert.True(harness.Runtime.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));

        IStateSnapshot<int> snapshot = await harness.Runtime.State(Timed).RefreshAsync();

        // No report: a load that throws becomes a fault snapshot, not an outcome. Decision 74.
        Assert.Empty(diagnostics.ReadLoadDiagnostics().Reports);
        Assert.Equal(0L, diagnostics.ReadLoadDiagnostics().Completed);

        // And the fault snapshot's stored error is still the plain aggregate, which is exactly why
        // the reports travel on Data rather than on a subclass. Decision 85. StateError's positional
        // members are Code/Message/ExceptionType/Detail/IsTransient (src/Statesman.Abstractions/Ledger.cs:5-10).
        Assert.NotNull(snapshot.Error);
        Assert.Equal("loader-failed", snapshot.Error!.Code);
        Assert.Equal("System.AggregateException", snapshot.Error.ExceptionType);
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
