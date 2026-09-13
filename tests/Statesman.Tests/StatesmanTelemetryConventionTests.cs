using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// Pins the OpenTelemetry-adjacent surface <see cref="StatesmanTelemetry"/> publishes: the source
/// names, the exact set of meter instruments (by name, kind, and unit), and the exact tag keys. An
/// instrument added, renamed, or retyped without an explicit decision must fail this test — that is the
/// whole reason it exists (see <c>docs/reference/telemetry.md</c>, which this test's name is cited from).
/// </summary>
public sealed class StatesmanTelemetryConventionTests
{
    [Fact]
    public void Source_names_are_exactly_Statesman()
    {
        Assert.Equal("Statesman", StatesmanTelemetry.ActivitySourceName);
        Assert.Equal("Statesman", StatesmanTelemetry.MeterName);
    }

    [Fact]
    public void The_meter_publishes_exactly_the_documented_instruments()
    {
        var observed = new HashSet<InstrumentSignature>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (!ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter))
            {
                return;
            }

            observed.Add(InstrumentSignature.From(instrument));
            meterListener.EnableMeasurementEvents(instrument);
        };

        // Every counter and histogram on StatesmanTelemetry is a static-field initializer, so all nine
        // are created together, eagerly, the first time anything touches a static member of the type
        // — never lazily per instrument. The fields themselves are internal (this repository ships
        // zero [InternalsVisibleTo]), so this test cannot name them or call Add/Record on them
        // directly; touching the public Meter property is enough to force the type's static
        // constructor, and with it every instrument's creation, so MeterListener.Start() discovers the
        // real, already-published instruments below rather than test-created stand-ins. Whether this
        // has already happened because an earlier test touched the runtime first does not matter:
        // MeterListener.Start() discovers instruments already published on a matching Meter exactly as
        // it discovers ones published afterward.
        _ = StatesmanTelemetry.Meter;
        listener.Start();

        var expected = new HashSet<InstrumentSignature>
        {
            new("statesman.reads", "Counter<Int64>", null),
            new("statesman.commits", "Counter<Int64>", null),
            new("statesman.refreshes", "Counter<Int64>", null),
            new("statesman.conflicts", "Counter<Int64>", null),
            new("statesman.faults", "Counter<Int64>", null),
            new("statesman.maintenance.failures", "Counter<Int64>", null),
            new("statesman.maintenance.failures.suppressed", "Counter<Int64>", null),
            new("statesman.maintenance.failures.dropped", "Counter<Int64>", null),
            new("statesman.operation.duration", "Histogram<Double>", "ms"),
        };

        Assert.Equal(expected, observed);
    }

    private static readonly StateKey<int> Counter = StateKey.Define<int>("telemetry-convention/counter");

    [Fact]
    public async Task Tags_produces_exactly_the_four_documented_keys()
    {
        // StatesmanTelemetry.Tags is internal (this repository ships zero [InternalsVisibleTo]), so
        // this test cannot call it directly from a different assembly. Instead it drives a real commit
        // through the public API and reads the tag keys StateHandle actually attaches to the
        // statesman.commits measurement — the same call site Tags exists to serve. Any concurrently
        // running test's own commit produces an identical key set, so which commit this test observes
        // does not matter; only the key set does.
        string[]? observedKeys = null;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter) && instrument.Name == "statesman.commits")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "statesman.commits" && observedKeys is null)
            {
                observedKeys = tags.ToArray().Select(tag => tag.Key).ToArray();
                captured.TrySetResult();
            }
        });
        listener.Start();

        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("telemetry-convention")
            .State(Counter, state => state.Initial(0))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);
        await harness.Runtime.State(Counter).SetAsync(1);

        await captured.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new[] { "statesman.root", "statesman.state", "statesman.partition", "statesman.operation" },
            observedKeys);
    }

    /// <summary>One observed instrument's name, kind, and unit — what the pinning gate compares.</summary>
    private sealed record InstrumentSignature(string Name, string Kind, string? Unit)
    {
        public static InstrumentSignature From(Instrument instrument)
        {
            string kind = instrument switch
            {
                Counter<long> => "Counter<Int64>",
                Histogram<double> => "Histogram<Double>",
                _ => instrument.GetType().Name,
            };
            return new InstrumentSignature(instrument.Name, kind, string.IsNullOrEmpty(instrument.Unit) ? null : instrument.Unit);
        }
    }
}
