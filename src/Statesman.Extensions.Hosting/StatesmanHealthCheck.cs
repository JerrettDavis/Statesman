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
/// Degraded when maintenance failures are retained, or when a store is running interval maintenance
/// without a lease — both mean the authoritative transitions are still landing while something around
/// them is not. Healthy otherwise.
/// </para>
/// <para>
/// <see cref="HealthCheckResult.Data"/> is keyed by the same <c>statesman.</c> names the loader
/// metadata and the meter already use, so a future load-diagnostics surface adds keys here rather than
/// changing this shape.
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

            MaintenanceFailureDiagnostics snapshot = diagnostics!.ReadMaintenanceFailures();
            retained += snapshot.Retained.Count;
            suppressed += snapshot.Suppressed;
            dropped += snapshot.Dropped;
            degraded.AddRange(snapshot.DegradedMaintenanceStores);
        }

        data["statesman.roots"] = _registry.All.Count;
        data["statesman.roots.uninitialized"] = uninitialized.Count;
        data["statesman.maintenance.failures.retained"] = retained;
        data["statesman.maintenance.failures.suppressed"] = suppressed;
        data["statesman.maintenance.failures.dropped"] = dropped;
        data["statesman.maintenance.stores.degraded"] = degraded.Count;

        if (uninitialized.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Statesman runtimes not initialized: {string.Join(", ", uninitialized)}.", data: data));
        }

        if (retained > 0 || degraded.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Statesman retained {retained} maintenance failure(s); {degraded.Count} store(s) maintaining without a lease.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Statesman is initialized with no retained maintenance failures.", data));
    }
}
