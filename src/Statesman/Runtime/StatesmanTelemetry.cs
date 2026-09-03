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

    internal static Histogram<double> OperationDuration { get; } =
        Meter.CreateHistogram<double>("statesman.operation.duration", unit: "ms");

    internal static TagList Tags(StateAddress address, string operation) => new()
    {
        { "statesman.root", address.Root },
        { "statesman.state", address.Path.Value },
        { "statesman.partition", address.Partition.Value },
        { "statesman.operation", operation },
    };
}
