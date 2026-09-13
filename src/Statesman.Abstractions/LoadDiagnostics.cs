namespace Statesman;

/// <summary>Whether one declared state source contributed to a load.</summary>
public enum StateSourceLoadStatus
{
    /// <summary>The source fetched and projected its result without throwing.</summary>
    Ready = 0,

    /// <summary>The source threw while fetching, or while projecting what it fetched.</summary>
    Faulted = 1,
}

/// <summary>
/// What one declared state source did during one load: whether it contributed, how long it took, and
/// what it threw when it did not.
/// </summary>
/// <remarks>
/// Per-source timing lives here and on the <c>statesman.load.source.duration</c> meter instrument, and
/// deliberately <b>not</b> on the stored record. A per-source metadata key is unbounded in the number
/// of sources a state declares, and every provider persists a record's metadata dictionary as JSON on
/// the head record and on every history record — so the record carries three bounded whole-load keys
/// instead. ROADMAP 0.3 pre-Phase-17 addendum decision 68.
/// </remarks>
public sealed record StateSourceLoadReport
{
    /// <summary>The source's declared name, as it appears in the manifest.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the source contributed to the loaded value.</summary>
    public required StateSourceLoadStatus Status { get; init; }

    /// <summary>How long this source took, measured on the runtime's own <see cref="TimeProvider"/>.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>The full name of the exception type the source threw, or <see langword="null"/> when it did not throw.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The message of the exception the source threw, or <see langword="null"/> when it did not throw.</summary>
    public string? ExceptionMessage { get; init; }
}

/// <summary>
/// One completed load of one state address: when it ran, how long it took, how complete it was, and
/// what each declared source did.
/// </summary>
/// <remarks>
/// A load that throws outright produces no report. <c>RequireAll</c> with any source failure, and a
/// <c>BestEffort</c> load where no source produced state and no initial value applied, both leave the
/// loader before an outcome exists and become a fault snapshot instead, which the
/// <c>statesman.faults</c> counter and the snapshot's own <c>StateError</c> already surface. A
/// <c>partial</c> or <c>initial-fallback</c> load completes and does report, which is where the
/// faulted-source detail an operator wants actually lives. Addendum decision 74.
/// </remarks>
public sealed record StateLoadReport
{
    /// <summary>The address that was loaded.</summary>
    public required StateAddress Address { get; init; }

    /// <summary>When the load began, on the runtime's clock.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the load finished, on the runtime's clock. Always exactly <see cref="StartedAt"/> plus
    /// <see cref="Elapsed"/>, so the three can never disagree.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>How long the whole load took.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// The same value the <c>statesman.load.completeness</c> record metadata key carries: one of
    /// <c>complete</c>, <c>partial</c>, <c>initial-fallback</c>, <c>seeded</c> or <c>retained</c>.
    /// </summary>
    public required string Completeness { get; init; }

    /// <summary>How many declared sources contributed.</summary>
    public required int SourcesReady { get; init; }

    /// <summary>How many declared sources threw.</summary>
    public required int SourcesFaulted { get; init; }

    /// <summary>One entry per source that ran, in declaration order.</summary>
    public required IReadOnlyList<StateSourceLoadReport> Sources { get; init; }
}

/// <summary>
/// A point-in-time view of a runtime's retained load reports: the latest completed load per address,
/// bounded by <c>StatesmanDiagnostics.MaxRetainedLoadReports</c>.
/// </summary>
public sealed record LoadDiagnostics
{
    /// <summary>Every load that completed since the runtime was created.</summary>
    public required long Completed { get; init; }

    /// <summary>Retained reports the bound evicted, oldest completion first.</summary>
    public required long Evicted { get; init; }

    /// <summary>The retained reports, oldest completion first.</summary>
    public required IReadOnlyList<StateLoadReport> Reports { get; init; }
}
