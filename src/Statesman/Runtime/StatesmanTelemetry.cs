using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Statesman;

public static class StatesmanTelemetry
{
    public const string ActivitySourceName = "Statesman";
    public const string MeterName = "Statesman";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public static Meter Meter { get; } = new(MeterName);

    internal static Counter<long> Reads { get; } = Meter.CreateCounter<long>("statesman.reads");

    internal static Counter<long> Commits { get; } = Meter.CreateCounter<long>("statesman.commits");

    internal static Counter<long> Refreshes { get; } = Meter.CreateCounter<long>("statesman.refreshes");

    internal static Counter<long> Conflicts { get; } = Meter.CreateCounter<long>("statesman.conflicts");

    internal static Counter<long> Faults { get; } = Meter.CreateCounter<long>("statesman.faults");

    // Maintenance failures are not converted into state faults, because the authoritative state
    // transition already succeeded (docs/reference/diagnostics.md). They are counted separately so an
    // application can surface them through its normal metrics infrastructure, which is what
    // docs/operations/observability.md tells applications to do -- and, before ROADMAP 0.3 Phase 15,
    // what nothing made possible. Three counters rather than one, because the disjoint meanings each
    // holds are not derivable from the others: "failures" counts every reported failure regardless of
    // outcome, "failures.suppressed" counts what the per-source rate limit (ROADMAP 0.3 Phase 16)
    // refused to retain before it was ever enqueued, and "failures.dropped" counts what the retention
    // bound evicted after a failure was retained. A failure is suppressed or (eventually) dropped,
    // never both.
    internal static Counter<long> MaintenanceFailuresReported { get; } =
        Meter.CreateCounter<long>("statesman.maintenance.failures");

    internal static Counter<long> MaintenanceFailuresDropped { get; } =
        Meter.CreateCounter<long>("statesman.maintenance.failures.dropped");

    internal static Counter<long> MaintenanceFailuresSuppressed { get; } =
        Meter.CreateCounter<long>("statesman.maintenance.failures.suppressed");

    internal static Histogram<double> OperationDuration { get; } =
        Meter.CreateHistogram<double>("statesman.operation.duration", unit: "ms");

    // Milliseconds, matching statesman.operation.duration rather than the OpenTelemetry convention's
    // seconds. Deliberate: docs/reference/telemetry.md records the ms deviation as kept through the
    // whole 0.x line, and shipping a second duration in seconds would put two unit conventions inside
    // one meter and turn the 1.0 correction into two migrations instead of one. Pre-Phase-17 addendum
    // decision 70.
    //
    // Per SOURCE, not per load. The whole-load interval is already statesman.operation.duration under
    // operation "refresh"; a second instrument over the same interval would be two names for one
    // number. The per-source breakdown is the thing no existing instrument carries, and it is here
    // rather than on the stored record because a metrics backend is where high cardinality is the
    // operator's own explicit choice.
    internal static Histogram<double> LoadSourceDuration { get; } =
        Meter.CreateHistogram<double>("statesman.load.source.duration", unit: "ms");

    internal static TagList Tags(StateAddress address, string operation) => new()
    {
        { "statesman.root", address.Root },
        { "statesman.state", address.Path.Value },
        { "statesman.partition", address.Partition.Value },
        { "statesman.operation", operation },
    };

    // The four documented keys plus the source name. A fifth key rather than folding the source into
    // the operation, because "operation" already names what the runtime was doing and a dashboard that
    // groups by it must keep working unchanged.
    internal static TagList SourceTags(StateAddress address, string operation, string source)
    {
        TagList tags = Tags(address, operation);
        tags.Add("statesman.source", source);
        return tags;
    }
}
