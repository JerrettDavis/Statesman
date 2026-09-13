using System.Diagnostics.CodeAnalysis;

namespace Statesman;

/// <summary>
/// One maintenance failure the runtime retained. A maintenance failure is a pruning or cache-repair
/// error raised <i>after</i> an append was already accepted, so it never turns an accepted state
/// transition into a fault; it is reported, counted, and — subject to the rate limit and the
/// retention bound — kept here for an operator to read.
/// </summary>
public sealed record MaintenanceFailure
{
    /// <summary>When the failure was reported, on the runtime's own clock.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The name of the ledger store whose maintenance failed.</summary>
    public required string StoreName { get; init; }

    /// <summary>The exception the store raised.</summary>
    public required Exception Exception { get; init; }
}

/// <summary>
/// A point-in-time view of a runtime's retained maintenance failures. Reading is a snapshot: the
/// counts describe the runtime's whole lifetime, while <see cref="Retained"/> holds only what the
/// rate limit admitted and the retention bound has not yet evicted.
/// </summary>
public sealed record MaintenanceFailureDiagnostics
{
    /// <summary>Every maintenance failure reported since the runtime was created.</summary>
    public required long Reported { get; init; }

    /// <summary>Failures the per-source rate limit refused to retain.</summary>
    public required long Suppressed { get; init; }

    /// <summary>Retained failures the retention bound later evicted, oldest first.</summary>
    public required long Dropped { get; init; }

    /// <summary>The failures still retained, oldest first, never more than <see cref="StatesmanDiagnostics.MaxRetainedMaintenanceFailures"/>.</summary>
    public required IReadOnlyList<MaintenanceFailure> Retained { get; init; }

    /// <summary>Ledger stores running interval maintenance without a lease, because no lease provider was available.</summary>
    public required IReadOnlyList<string> DegradedMaintenanceStores { get; init; }
}

/// <summary>The fixed limits the runtime applies to maintenance-failure retention.</summary>
/// <remarks>
/// Constants rather than options. ROADMAP 0.3 Phase 15 shipped the retention bound as a private
/// literal and Phase 16 makes it public so documentation and tests can cite a symbol instead of
/// repeating a number. Making either value configurable would need a runtime options object
/// <c>StatesmanDeclaration.CreateRuntime</c> does not have, which turns an additive change into a
/// builder change; pre-Phase-16 addendum decision 57.
/// </remarks>
public static class StatesmanDiagnostics
{
    /// <summary>
    /// How many maintenance-failure exceptions a runtime retains. Sixty-four: large enough that a
    /// burst of genuinely distinct failures is still legible, and small enough that the retained set
    /// can never itself be the leak.
    /// </summary>
    public const int MaxRetainedMaintenanceFailures = 64;

    /// <summary>
    /// How many maintenance failures <i>per ledger store</i> are retained per
    /// <see cref="MaintenanceFailureRateWindow"/>. Sixteen rather than sixty-four so the rate limit
    /// and the retention bound stay independently observable: at sixty-four, nothing could ever reach
    /// the bound inside one window and the two mechanisms would collapse into one number.
    /// </summary>
    public const int MaintenanceFailureRate = 16;

    /// <summary>The window <see cref="MaintenanceFailureRate"/> applies over.</summary>
    public static TimeSpan MaintenanceFailureRateWindow { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many completed load reports a runtime retains, one per address, newest completion kept.
    /// Sixty-four, matching <see cref="MaxRetainedMaintenanceFailures"/> so an operator learns one
    /// number rather than two. A bound rather than a plain map because a partitioned state's address
    /// space is unbounded, and a report holds a per-source list — the same leak ROADMAP 0.3 Phase 15
    /// closed for retained exceptions, and a larger one.
    /// </summary>
    public const int MaxRetainedLoadReports = 64;
}

/// <summary>
/// Optional diagnostics a runtime can expose. Discovered with
/// <see cref="StatesmanDiagnosticsExtensions.TryGetDiagnostics"/> rather than declared on
/// <see cref="IStatesman"/>, so it is purely additive to a published interface.
/// </summary>
/// <remarks>
/// This is deliberately <b>not</b> an <see cref="IStateCapability"/>. Capabilities are negotiated on a
/// ledger store and are enumerated by the capability-matrix test, which would demand a provider column
/// for something no provider implements. <c>IStateCapabilityProvider</c> is excluded for the same
/// reason. Pre-Phase-16 addendum decision 64.
///
/// Two families live here rather than in two interfaces — maintenance failures since ROADMAP 0.3
/// Phase 16, load reports since Phase 17 — so a consumer has one discovery call and one object for a
/// runtime's diagnostics. Adding members is free against the published baseline because this interface
/// post-dates the <c>0.3.0</c> tag the baseline names; pre-Phase-17 addendum decision 69.
/// </remarks>
public interface IStatesmanDiagnostics
{
    /// <summary>Reads a snapshot of this runtime's maintenance-failure diagnostics.</summary>
    MaintenanceFailureDiagnostics ReadMaintenanceFailures();

    /// <summary>
    /// Clears the retained failures, leaving the lifetime counts alone. An application that drains
    /// these into its own logging or health infrastructure calls this after a successful drain.
    /// </summary>
    /// <returns>How many retained failures were cleared.</returns>
    int ClearMaintenanceFailures();

    /// <summary>
    /// Reads a snapshot of this runtime's retained load reports: the latest completed load per address,
    /// oldest completion first, bounded by
    /// <see cref="StatesmanDiagnostics.MaxRetainedLoadReports"/>.
    /// </summary>
    /// <returns>The snapshot.</returns>
    LoadDiagnostics ReadLoadDiagnostics();

    /// <summary>
    /// Clears the retained load reports, leaving the lifetime counts alone. An application that drains
    /// these into its own infrastructure calls this after a successful drain.
    /// </summary>
    /// <returns>How many retained reports were cleared.</returns>
    int ClearLoadDiagnostics();
}

/// <summary>Discovery for <see cref="IStatesmanDiagnostics"/>.</summary>
public static class StatesmanDiagnosticsExtensions
{
    /// <summary>
    /// Returns the runtime's diagnostics surface when it has one. The shipped runtime always does; a
    /// test double or a future alternative runtime need not.
    /// </summary>
    /// <param name="statesman">The runtime.</param>
    /// <param name="diagnostics">The diagnostics surface, when supported.</param>
    /// <returns><see langword="true"/> when <paramref name="statesman"/> exposes diagnostics.</returns>
    public static bool TryGetDiagnostics(
        this IStatesman statesman, [NotNullWhen(true)] out IStatesmanDiagnostics? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(statesman);
        diagnostics = statesman as IStatesmanDiagnostics;
        return diagnostics is not null;
    }
}
