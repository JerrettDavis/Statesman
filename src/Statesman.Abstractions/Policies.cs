namespace Statesman;

public enum StateContainerIsolation
{
    Attached = 0,
    Isolated = 1,
}

public enum StateStatus
{
    Absent = 0,
    Ready = 1,
    Stale = 2,
    Invalidated = 3,
    Cleared = 4,
    Faulted = 5,
}

public enum StateOperation
{
    Seeded = 0,
    Set = 1,
    Transitioned = 2,
    Refreshed = 3,
    Invalidated = 4,
    Cleared = 5,
    Faulted = 6,
    Imported = 7,
}

public enum StateReadMode
{
    Cached = 0,
    Current = 1,
    RefreshIfStale = 2,
    Fresh = 3,
}

public enum StateSourceExecution
{
    Sequential = 0,
    Parallel = 1,
}

public enum StateSourceFailureMode
{
    RequireAll = 0,
    BestEffort = 1,
}

public enum StateFaultBehavior
{
    KeepLastKnown = 0,
    ReplaceWithFault = 1,
}

public enum StateWriteBehavior
{
    RecordAll = 0,
    SuppressEquivalent = 1,
}

public sealed record StateFreshnessPolicy
{
    public static StateFreshnessPolicy Default { get; } = new();

    public TimeSpan FreshFor { get; init; } = TimeSpan.MaxValue;

    public TimeSpan ServeStaleFor { get; init; } = TimeSpan.Zero;

    public bool RefreshStaleInBackground { get; init; }

    public void Validate()
    {
        if (FreshFor < TimeSpan.Zero)
        {
            throw new InvalidOperationException("FreshFor cannot be negative.");
        }

        if (ServeStaleFor < TimeSpan.Zero)
        {
            throw new InvalidOperationException("ServeStaleFor cannot be negative.");
        }
    }
}

public sealed record StateRetentionPolicy
{
    public static StateRetentionPolicy KeepAll { get; } = new();

    public int? MaxRevisions { get; init; }

    public TimeSpan? MaxAge { get; init; }

    public long? MaxBytes { get; init; }

    public bool KeepTombstones { get; init; } = true;

    public void Validate()
    {
        if (MaxRevisions is <= 0)
        {
            throw new InvalidOperationException("MaxRevisions must be greater than zero when specified.");
        }

        if (MaxAge is TimeSpan maxAge && maxAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("MaxAge must be greater than zero when specified.");
        }

        if (MaxBytes is <= 0)
        {
            throw new InvalidOperationException("MaxBytes must be greater than zero when specified.");
        }
    }
}

public sealed record StateRefreshPolicy
{
    public static StateRefreshPolicy None { get; } = new();

    public bool OnFirstRead { get; init; }

    public bool WhenStale { get; init; }

    public bool WarmOnStart { get; init; }

    public IReadOnlyList<StatePartition> WarmPartitions { get; init; } = Array.Empty<StatePartition>();

    public TimeSpan? Interval { get; init; }

    public IReadOnlySet<string> Signals { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Interval is TimeSpan interval && interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A refresh interval must be greater than zero.");
        }
    }
}

public sealed record StateReadOptions
{
    public static StateReadOptions Cached { get; } = new() { Mode = StateReadMode.Cached };

    public static StateReadOptions Current { get; } = new() { Mode = StateReadMode.Current };

    public static StateReadOptions Fresh { get; } = new() { Mode = StateReadMode.Fresh };

    public StateReadMode Mode { get; init; } = StateReadMode.Current;

    public bool AllowLastKnownOnFault { get; init; } = true;
}

public sealed record StateWriteOptions
{
    public string Source { get; init; } = "application";

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public long? ExpectedRevision { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record StateHistoryOptions
{
    public int? Take { get; init; } = 100;

    public long? BeforeRevision { get; init; }

    public DateTimeOffset? Since { get; init; }

    public bool NewestFirst { get; init; } = true;

    public void Validate()
    {
        if (Take is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Take), "Take must be greater than zero when specified.");
        }

        if (BeforeRevision is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BeforeRevision), "BeforeRevision must be greater than zero when specified.");
        }
    }
}

/// <summary>
/// How much of an <see cref="IStateChangeFeed"/> one <see cref="IStateChangeFeed.ReadAsync"/> call
/// yields. The sibling of <see cref="StateHistoryOptions"/> for the cross-stream feed, so "bounded
/// read" has one vocabulary across both enumerating APIs.
/// </summary>
public sealed record StateChangeReadOptions
{
    /// <summary>Reads to the feed's tail — <see cref="Take"/> is <see langword="null"/>. Prefer this to allocating a fresh instance per read.</summary>
    public static StateChangeReadOptions Default { get; } = new();

    /// <summary>
    /// The most records one read yields. <see langword="null"/> (the default) reads to the feed's
    /// tail. Deliberately <b>not</b> the 100 that <see cref="StateHistoryOptions.Take"/> defaults to:
    /// on a resumable feed a capped default would make a consumer with a large backlog look as though
    /// it had stalled, where the history default merely truncates a single stream.
    /// </summary>
    public int? Take { get; init; }

    /// <summary>Throws if any value is outside its supported range.</summary>
    public void Validate()
    {
        if (Take is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Take), "Take must be greater than zero when specified.");
        }
    }
}

public sealed record StateObservationOptions
{
    public bool IncludeCurrent { get; init; } = true;

    public int BufferCapacity { get; init; } = 64;

    public void Validate()
    {
        if (BufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferCapacity), "BufferCapacity must be greater than zero.");
        }
    }
}
