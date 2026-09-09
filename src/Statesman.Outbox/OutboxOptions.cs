namespace Statesman.Outbox;

/// <summary>Everything one outbox is configured with.</summary>
public sealed class OutboxOptions
{
    /// <summary>Identifies this outbox's cursor, and appears in its lease id. Two outboxes over one store must differ here.</summary>
    public string OutboxId { get; set; } = "default";

    /// <summary>The registered ledger store name to read the change feed from.</summary>
    public string StoreName { get; set; } = "memory";

    /// <summary>The Statesman root whose manifest supplies <see cref="Fingerprint"/>, and the first segment of the lease id. Null runs the outbox over a bare store.</summary>
    public string? Root { get; set; }

    /// <summary>The declaration fingerprint stamped on every message. Populated from the root's manifest when <see cref="Root"/> is set and this is null.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>The media type stamped on a non-null payload. The payload is never inspected, so this is declared, not inferred.</summary>
    public string PayloadContentType { get; set; } = "application/json";

    /// <summary>The most records published in one <see cref="IStateChangeSink.PublishAsync"/> call, and the most the change feed is asked to yield in one read. The dispatcher pages the feed at this size until a page comes back empty.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// When true (the default), a store without <see cref="IStateLeaseProvider"/> is refused with
    /// <see cref="NotSupportedException"/>. Setting it false runs unleased, which is safe only when
    /// exactly one process ever dispatches this outbox.
    /// </summary>
    public bool RequireLease { get; set; } = true;

    /// <summary>The lease time-to-live. The dispatcher renews it while publishing rather than letting it lapse mid-cycle.</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the dispatcher waits between lease renewals while <b>holding</b> the lease. It governs
    /// both the renewal at the top of each dispatch cycle and the renewals between batches within one
    /// drain. Null (the default) means a third of <see cref="LeaseTtl"/>.
    /// <see cref="TimeSpan.Zero"/> renews at the top of every cycle and before every batch after the
    /// first — the setting tests use to make renewal deterministic, and a legitimate production choice
    /// for a very slow sink. Renewal is checked once per cycle and a cycle runs at least once per
    /// <see cref="PollInterval"/>, so worst-case renewal lateness is one <see cref="PollInterval"/>; a
    /// deployment setting <see cref="PollInterval"/> at or above <see cref="LeaseTtl"/> degenerates to
    /// re-acquiring the lease every cycle rather than holding it.
    /// </summary>
    public TimeSpan? LeaseRenewInterval { get; set; }

    /// <summary>The effective renewal interval: <see cref="LeaseRenewInterval"/>, or a third of <see cref="LeaseTtl"/>.</summary>
    public TimeSpan EffectiveLeaseRenewInterval => LeaseRenewInterval ?? (LeaseTtl / 3);

    /// <summary>
    /// How long the hosted worker waits between dispatch cycles when nothing wakes it — the floor on
    /// dispatch latency, not the only trigger. A store implementing <see cref="IStateChangeNotifier"/>
    /// also wakes the worker on a pushed hint, so a change is dispatched without waiting this out; a
    /// store without one polls on this interval alone. A missed hint costs at most one interval.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The first delay after a failed cycle.</summary>
    public TimeSpan MinRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The delay ceiling for consecutive failures. Must stay below <see cref="LeaseTtl"/> so a stalled worker releases its lease rather than holding it while doing nothing.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Skip a batch the sink keeps rejecting after this many consecutive attempts. Null (the
    /// default) never skips: a poison record blocks the cursor visibly rather than being dropped
    /// silently.
    /// </summary>
    public int? SkipPoisonAfterAttempts { get; set; }

    /// <summary>Throws if any value is outside its supported range.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutboxId, nameof(OutboxId));
        ArgumentException.ThrowIfNullOrWhiteSpace(StoreName, nameof(StoreName));
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadContentType, nameof(PayloadContentType));

        if (BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, "The outbox batch size must be greater than zero.");
        }

        if (LeaseTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseTtl), LeaseTtl, "The outbox lease time-to-live must be greater than zero.");
        }

        if (LeaseRenewInterval is { } renewInterval && (renewInterval < TimeSpan.Zero || renewInterval >= LeaseTtl))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewInterval), renewInterval, "The outbox lease renewal interval must be at least zero and below the lease time-to-live.");
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "The outbox poll interval must be greater than zero.");
        }

        if (MinRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinRetryDelay), MinRetryDelay, "The outbox minimum retry delay must be greater than zero.");
        }

        if (MaxRetryDelay < MinRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), MaxRetryDelay, "The outbox maximum retry delay must be at least the minimum retry delay.");
        }

        if (MaxRetryDelay >= LeaseTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), MaxRetryDelay, "The outbox maximum retry delay must be below the lease time-to-live so a stalled worker releases its lease.");
        }

        if (SkipPoisonAfterAttempts is { } attempts && attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SkipPoisonAfterAttempts), attempts, "The poison-skip attempt count must be greater than zero when set.");
        }
    }
}
