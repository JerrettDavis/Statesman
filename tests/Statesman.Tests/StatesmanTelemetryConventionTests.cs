using System.Diagnostics.Metrics;
using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// Pins the OpenTelemetry-adjacent surface <see cref="StatesmanTelemetry"/> publishes: the source
/// names, the exact set of meter instruments (by name, kind, and unit), and the exact tag keys. An
/// instrument added, renamed, or retyped without an explicit decision must fail this test — that is the
/// whole reason it exists (see <c>docs/reference/telemetry.md</c>, which this test's name is cited from).
/// </summary>
[Collection(nameof(MaintenanceFailureMeterCollection))]
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

        // Ordinal order over the whole pipe-joined row, not just the instrument name: '.' (0x2E) sorts
        // below '|' (0x7C), so "…failures.dropped|…" and "…failures.suppressed|…" sort ahead of the
        // plain "…failures|…" row, whose next character after the shared prefix is the pipe itself.
        string[] expected =
        [
            "statesman.commits|Counter<Int64>|(none)",
            "statesman.conflicts|Counter<Int64>|(none)",
            "statesman.faults|Counter<Int64>|(none)",
            "statesman.maintenance.failures.dropped|Counter<Int64>|(none)",
            "statesman.maintenance.failures.suppressed|Counter<Int64>|(none)",
            "statesman.maintenance.failures|Counter<Int64>|(none)",
            "statesman.operation.duration|Histogram<Double>|ms",
            "statesman.reads|Counter<Int64>|(none)",
            "statesman.refreshes|Counter<Int64>|(none)",
        ];

        // Sorted arrays rather than two HashSets. The set comparison has identical semantics here —
        // every signature is distinct — but xUnit truncates a set-equality failure message, so a
        // renamed instrument produced an unreadable diff. An ordered array names exactly which row
        // moved.
        string[] observedSorted = [.. observed.Select(signature => signature.ToString()).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, observedSorted);
    }

    private static readonly string[] PerStateTagKeys =
        ["statesman.root", "statesman.state", "statesman.partition", "statesman.operation"];

    /// <summary>
    /// Which tag keys each instrument's measurements must carry. The map — rather than one hard-coded
    /// key array — is what lets a later instrument with a different tag set add a row instead of
    /// rewriting this fact. <c>docs/reference/telemetry.md</c> is the document this pins.
    /// </summary>
    /// <remarks>
    /// Declared <b>after</b> <c>PerStateTagKeys</c>, and that order is load-bearing: static field
    /// initializers run in declaration order, so a map initialized first would capture a null array.
    /// </remarks>
    private static readonly Dictionary<string, string[]> ExpectedTagKeys = new(StringComparer.Ordinal)
    {
        ["statesman.reads"] = PerStateTagKeys,
        ["statesman.commits"] = PerStateTagKeys,
        ["statesman.refreshes"] = PerStateTagKeys,
        ["statesman.conflicts"] = PerStateTagKeys,
        ["statesman.faults"] = PerStateTagKeys,
        ["statesman.operation.duration"] = PerStateTagKeys,
    };

    private static readonly StateKey<int> TagCounter = StateKey.Define<int>("telemetry-convention/counter");
    private static readonly StateKey<int> TagLoaded = StateKey.Define<int>("telemetry-convention/loaded");

    [Fact]
    public async Task Every_documented_instrument_carries_exactly_its_documented_tag_keys()
    {
        // StatesmanTelemetry.Tags is internal (this repository ships zero [InternalsVisibleTo]), so
        // this test cannot call it directly from a different assembly. Instead it drives one runtime
        // through every call site that records a measurement — a read, a commit, a refresh, a conflict
        // and a fault — and reads the keys StateHandle actually attaches. Before ROADMAP 0.3 Phase 17
        // this fact observed statesman.commits alone, while the documentation claimed all six.
        var observed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, StatesmanTelemetry.Meter))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            lock (observed)
            {
                observed[instrument.Name] = [.. tags.ToArray().Select(tag => tag.Key)];
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            lock (observed)
            {
                observed[instrument.Name] = [.. tags.ToArray().Select(tag => tag.Key)];
            }
        });
        listener.Start();

        var loader = new FailingLoader();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("telemetry-convention")
            .State(TagCounter, state => state.Initial(0))
            .State(TagLoaded, state => state
                .Initial(0)
                .Load(load => load
                    .From<FailingLoader, int>("always-fails", (service, _, cancellationToken) =>
                        service.FetchAsync(cancellationToken))
                    .Into((_, value, _) => value)
                    .RequireAll()))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add(loader));

        // statesman.commits and statesman.operation.duration.
        await harness.Runtime.State(TagCounter).SetAsync(1);
        // statesman.reads.
        _ = await harness.Runtime.State(TagCounter).GetAsync();
        // statesman.conflicts: the counter is added before StateConcurrencyException is thrown.
        await Assert.ThrowsAsync<StateConcurrencyException>(async () =>
            await harness.Runtime.State(TagCounter).SetAsync(
                2, new StateWriteOptions { Source = "test", ExpectedRevision = 9_999 }));
        // statesman.refreshes on the first load, then statesman.faults on the second, because the
        // loader succeeds once and throws afterwards.
        _ = await harness.Runtime.State(TagLoaded).RefreshAsync();
        IStateSnapshot<int> faulted = await harness.Runtime.State(TagLoaded).RefreshAsync();
        Assert.Equal(StateStatus.Faulted, faulted.Status);

        lock (observed)
        {
            Assert.Equal(
                ExpectedTagKeys.Keys.Order(StringComparer.Ordinal),
                observed.Keys.Where(ExpectedTagKeys.ContainsKey).Order(StringComparer.Ordinal));
            foreach ((string instrument, string[] expected) in ExpectedTagKeys)
            {
                Assert.Equal(expected, observed[instrument]);
            }
        }
    }

    /// <summary>A loader that succeeds once and throws on every later call, so one runtime produces both a refresh and a fault.</summary>
    private sealed class FailingLoader
    {
        private int _calls;

        public ValueTask<int> FetchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _calls) == 1
                ? ValueTask.FromResult(1)
                : throw new InvalidOperationException("always-fails");
        }
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

        /// <summary>The one-line form the pinning gate compares, so a failure names the row that moved.</summary>
        public override string ToString() => $"{Name}|{Kind}|{Unit ?? "(none)"}";
    }
}
