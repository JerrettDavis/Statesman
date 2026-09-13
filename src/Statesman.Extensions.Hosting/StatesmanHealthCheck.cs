using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Statesman;

/// <summary>
/// Reports a Statesman runtime's health from facts the runtime already keeps: whether it is
/// initialized, and what its maintenance-failure diagnostics say. It computes nothing and stores
/// nothing of its own.
/// </summary>
/// <remarks>
/// <para>
/// Unhealthy when the runtime is not initialized, because nothing it serves is authoritative yet.
/// Degraded when maintenance failures are retained, when a store is running interval maintenance
/// without a lease, or when a state's latest load did not complete — all three mean the authoritative
/// transitions are still landing while something around them is not. Healthy otherwise.
/// </para>
/// <para>
/// <see cref="HealthCheckResult.Data"/> is keyed by the same <c>statesman.</c> naming convention. The
/// load-diagnostics keys ROADMAP 0.3 Phase 17 added arrived exactly that way — as keys on this
/// dictionary rather than as a change to its shape — which is what pre-Phase-16 addendum decision 61
/// shaped this type for.
/// </para>
/// <para>
/// <c>IHealthChecksBuilder</c> and <c>AddCheck&lt;T&gt;</c> live in
/// <c>Microsoft.Extensions.Diagnostics.HealthChecks</c>, not in the <c>.Abstractions</c> package this
/// type ships against (measured against 10.0.11 during ROADMAP 0.3 Phase 16 Task 8: the abstractions
/// package's XML documentation lists no such type), so there is no <c>AddStatesman</c> registration
/// extension here. Register this check directly:
/// <c>services.AddHealthChecks().AddCheck&lt;StatesmanHealthCheck&gt;("statesman")</c>.
/// </para>
/// </remarks>
public sealed class StatesmanHealthCheck : IHealthCheck
{
    private readonly IStatesmanRegistry _registry;

    /// <summary>Creates the health check over every registered runtime.</summary>
    /// <param name="registry">The multi-root registry.</param>
    public StatesmanHealthCheck(IStatesmanRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var uninitialized = new List<string>();
        var degraded = new List<string>();
        long retained = 0;
        long suppressed = 0;
        long dropped = 0;
        int loadReports = 0;
        int incompleteLoads = 0;
        long faultedSources = 0;
        string slowestSource = string.Empty;
        TimeSpan slowest = TimeSpan.Zero;

        foreach (IStatesman statesman in _registry.All)
        {
            if (!statesman.IsInitialized)
            {
                uninitialized.Add(statesman.Id);
            }

            if (!statesman.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics))
            {
                continue;
            }

            MaintenanceFailureDiagnostics snapshot = diagnostics.ReadMaintenanceFailures();
            retained += snapshot.Retained.Count;
            suppressed += snapshot.Suppressed;
            dropped += snapshot.Dropped;
            degraded.AddRange(snapshot.DegradedMaintenanceStores);

            LoadDiagnostics loads = diagnostics.ReadLoadDiagnostics();
            loadReports += loads.Reports.Count;
            foreach (StateLoadReport report in loads.Reports)
            {
                if (IsIncomplete(report.Completeness))
                {
                    incompleteLoads++;
                }

                faultedSources += report.SourcesFaulted;
                foreach (StateSourceLoadReport source in report.Sources)
                {
                    if (source.Elapsed > slowest)
                    {
                        slowest = source.Elapsed;
                        slowestSource = source.Name;
                    }
                }
            }
        }

        data["statesman.roots"] = _registry.All.Count;
        data["statesman.roots.uninitialized"] = uninitialized.Count;
        data["statesman.maintenance.failures.retained"] = retained;
        data["statesman.maintenance.failures.suppressed"] = suppressed;
        data["statesman.maintenance.failures.dropped"] = dropped;
        data["statesman.maintenance.stores.degraded"] = degraded.Count;
        data["statesman.load.reports"] = loadReports;
        data["statesman.load.reports.incomplete"] = incompleteLoads;
        data["statesman.load.sources.faulted"] = faultedSources;
        data["statesman.load.slowest.source"] = slowestSource;
        data["statesman.load.slowest.duration.ms"] = slowest.TotalMilliseconds;

        if (uninitialized.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Statesman runtimes not initialized: {string.Join(", ", uninitialized)}.", data: data));
        }

        if (retained > 0 || degraded.Count > 0 || incompleteLoads > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Statesman retained {retained} maintenance failure(s); {degraded.Count} store(s) maintaining without a lease; " +
                $"{incompleteLoads} state(s) whose latest load did not complete.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Statesman is initialized, with no retained maintenance failures and every latest load complete.", data));
    }

    // "partial" means at least one declared source threw while others contributed; "initial-fallback"
    // means none contributed and the declared initial value carried the load. Both are Degraded rather
    // than Unhealthy: the state is usable and authoritative, it is simply not what the declaration
    // asked for, and taking a root out of a load balancer for serving its own declared fallback would
    // be the fallback working as designed being treated as an outage. "complete", "seeded" and
    // "retained" are healthy — the last two are what a declaration with no sources always reports.
    //
    // Self-correcting without a drain call: reports are retained latest-per-address, so the next
    // complete refresh of that address replaces the degraded one. Pre-Phase-17 addendum decision 71.
    private static bool IsIncomplete(string completeness) =>
        completeness is "partial" or "initial-fallback";
}
