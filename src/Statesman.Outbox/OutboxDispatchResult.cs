namespace Statesman.Outbox;

/// <summary>How one <see cref="StateChangeDispatcher.DispatchOnceAsync"/> cycle ended.</summary>
public enum OutboxDispatchOutcome
{
    /// <summary>The feed was drained to its tail and every batch was accepted.</summary>
    Completed = 0,

    /// <summary>Another worker holds the outbox lease. Expected under contention, not an error — nothing was published.</summary>
    LeaseUnavailable = 1,

    /// <summary>The lease was lost mid-cycle. Dispatch stopped before the next batch and the cursor was not advanced past unpublished records.</summary>
    LeaseLost = 2,
}

/// <summary>What one dispatch cycle did.</summary>
public sealed record OutboxDispatchResult
{
    /// <summary>How the cycle ended.</summary>
    public required OutboxDispatchOutcome Outcome { get; init; }

    /// <summary>Messages handed to the sink and accepted by it.</summary>
    public required int Published { get; init; }

    /// <summary>Batches the sink accepted.</summary>
    public required int Batches { get; init; }

    /// <summary>Messages advanced past without being delivered, because <see cref="OutboxOptions.SkipPoisonAfterAttempts"/> was reached. Always zero unless that option is set.</summary>
    public required int Skipped { get; init; }

    /// <summary>The cursor after the cycle, or <see langword="null"/> if nothing has ever been published.</summary>
    public required StateChangeCursor? Cursor { get; init; }
}
