using System.Diagnostics;
using System.Globalization;

namespace Statesman.Outbox;

/// <summary>
/// Reads a store's <see cref="IStateChangeFeed"/> from a persisted cursor and publishes every record
/// it reads to an <see cref="IStateChangeSink"/>, at least once, under a lease that keeps one
/// dispatcher at a time advancing the cursor.
/// </summary>
/// <remarks>
/// <para>
/// The promise this type makes, exactly: <b>every record this dispatcher reads from the feed is
/// handed to the sink at least once, and the persisted cursor never advances past a record the sink
/// has not accepted.</b> It publishes first and persists the cursor second, so a crash between the
/// two re-delivers rather than loses.
/// </para>
/// <para>
/// That is a promise about this loop, not about the ledger. Composed with the feed it becomes
/// at-least-once delivery of every record <em>the feed yields</em>, and the feed is documented to be
/// able to (a) permanently skip a record whose position was allocated before, but committed after, a
/// later record already consumed — the in-memory, filesystem, and Redis providers allocate the
/// position before the record is durably visible, under a per-address gate only — and (b) omit
/// records pruned before the outbox reached them, which on Entity Framework Core with any
/// non-default retention policy can happen milliseconds after the write. This dispatcher therefore
/// <b>cannot</b> claim that every committed state change is delivered. One configuration is
/// genuinely complete: <b>Entity Framework Core as the feed source with
/// <see cref="StateRetentionPolicy.KeepAll"/></b>, where the position and the record commit in one
/// serializable transaction and nothing is pruned. See <c>docs/guides/outbox.md</c>.
/// </para>
/// <para>
/// Two dispatchers running unleased over one store do not merely double-publish — they race the
/// cursor store, and a cursor write can move past records the other never published. That is why
/// <see cref="OutboxOptions.RequireLease"/> defaults to true and why every
/// <see cref="IOutboxCursorStore"/> write is monotonic.
/// </para>
/// <para>
/// <see cref="DispatchOnceAsync"/> is public so one cycle can be driven deterministically without a
/// hosted service and a real timer.
/// </para>
/// </remarks>
public sealed class StateChangeDispatcher
{
    private readonly IStateLedgerStore _store;
    private readonly IStateChangeFeed _feed;
    private readonly IStateLeaseProvider? _leases;
    private readonly IStateChangeSink _sink;
    private readonly IOutboxCursorStore _cursors;
    private readonly OutboxOptions _options;
    private StateChangeCursor? _poisonCursor;
    private int _poisonAttempts;

    /// <summary>Creates a dispatcher over one store, sink, and cursor store.</summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="store"/> does not implement <see cref="IStateChangeFeed"/>, or does not
    /// implement <see cref="IStateLeaseProvider"/> while <see cref="OutboxOptions.RequireLease"/> is
    /// set.
    /// </exception>
    public StateChangeDispatcher(
        IStateLedgerStore store,
        IStateChangeSink sink,
        IOutboxCursorStore cursors,
        OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!store.TryGetCapability(out IStateChangeFeed? feed))
        {
            throw new NotSupportedException(
                $"Store '{store.Name}' does not implement IStateChangeFeed, which the outbox requires to read changes.");
        }

        bool hasLeases = store.TryGetCapability(out IStateLeaseProvider? leases);
        if (options.RequireLease && !hasLeases)
        {
            throw new NotSupportedException(
                $"Store '{store.Name}' does not implement IStateLeaseProvider, which the outbox requires to keep one dispatcher at a time advancing the cursor. " +
                $"Set {nameof(OutboxOptions)}.{nameof(OutboxOptions.RequireLease)} to false to dispatch unleased, accepting that a second dispatcher can advance the cursor past records neither of them published.");
        }

        _store = store;
        _feed = feed;
        _leases = hasLeases ? leases : null;
        _sink = sink;
        _cursors = cursors;
        _options = options;

        LeaseId = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.Root ?? "statesman"}:{store.Name}:outbox:{options.OutboxId}");
        RunningWithoutLease = !hasLeases;
    }

    /// <summary>The configured <see cref="OutboxOptions.OutboxId"/>, which keys the cursor.</summary>
    public string OutboxId => _options.OutboxId;

    /// <summary>The lease this dispatcher acquires: <c>{root}:{store}:outbox:{outboxId}</c>. Two outboxes over one store do not exclude each other.</summary>
    public string LeaseId { get; }

    /// <summary>
    /// True when the store has no <see cref="IStateLeaseProvider"/> and
    /// <see cref="OutboxOptions.RequireLease"/> was turned off — documented degradation, safe only
    /// when exactly one process ever dispatches this outbox. The hosting layer logs it once.
    /// </summary>
    public bool RunningWithoutLease { get; }

    /// <summary>The exception the last cycle failed with, or <see langword="null"/> after a cycle that completed.</summary>
    public Exception? LastDispatchError { get; private set; }

    /// <summary>The exception that caused the most recent poison-batch skip, or <see langword="null"/> if none was ever skipped.</summary>
    public Exception? LastSkippedError { get; private set; }

    /// <summary>The sink this dispatcher publishes to. The hosted service disposes it; a caller driving <see cref="DispatchOnceAsync"/> directly owns that responsibility instead.</summary>
    public IStateChangeSink Sink => _sink;

    /// <summary>Runs exactly one dispatch cycle: take the lease, read the cursor, drain the feed, publish, advance.</summary>
    /// <remarks>Not safe to call concurrently on the same instance: the poison-attempt counter and lease-renewal tracking are unsynchronized instance state.</remarks>
    public async ValueTask<OutboxDispatchResult> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        IStateLease? lease = null;
        if (_leases is not null)
        {
            lease = await _leases.AcquireAsync(LeaseId, _options.LeaseTtl, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return new OutboxDispatchResult
                {
                    Outcome = OutboxDispatchOutcome.LeaseUnavailable,
                    Published = 0,
                    Batches = 0,
                    Skipped = 0,
                    Cursor = await _cursors.ReadAsync(_options.OutboxId, cancellationToken).ConfigureAwait(false),
                };
            }
        }

        try
        {
            return await DrainAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<OutboxDispatchResult> DrainAsync(IStateLease? lease, CancellationToken cancellationToken)
    {
        StateChangeCursor? cursor = await _cursors.ReadAsync(_options.OutboxId, cancellationToken).ConfigureAwait(false);
        TimeSpan renewInterval = _options.EffectiveLeaseRenewInterval;
        long renewedAt = Stopwatch.GetTimestamp();
        var batch = new List<StateChangeMessage>(_options.BatchSize);
        var outcome = OutboxDispatchOutcome.Completed;
        int published = 0;
        int batches = 0;
        int skipped = 0;

        try
        {
            StateChangeCursor? pending = null;
            await foreach (StateChangeEnvelope envelope in _feed.ReadAsync(cursor, cancellationToken).ConfigureAwait(false))
            {
                batch.Add(StateChangeMessage.FromRecord(
                    envelope.Record,
                    _store.Name,
                    _options.Fingerprint,
                    _options.PayloadContentType));
                pending = envelope.Cursor;

                if (batch.Count < _options.BatchSize)
                {
                    continue;
                }

                if (batches > 0)
                {
                    bool held;
                    (held, renewedAt) = await StillHeldAsync(lease, renewInterval, renewedAt, _options.LeaseTtl, cancellationToken).ConfigureAwait(false);
                    if (!held)
                    {
                        outcome = OutboxDispatchOutcome.LeaseLost;
                        break;
                    }
                }

                if (await PublishAndAdvanceAsync(batch, cursor, pending.Value, cancellationToken).ConfigureAwait(false))
                {
                    published += batch.Count;
                }
                else
                {
                    skipped += batch.Count;
                }

                batches++;
                cursor = pending;
                batch.Clear();
            }

            if (outcome == OutboxDispatchOutcome.Completed && batch.Count > 0 && pending is { } tail)
            {
                bool held = true;
                if (batches > 0)
                {
                    (held, renewedAt) = await StillHeldAsync(lease, renewInterval, renewedAt, _options.LeaseTtl, cancellationToken).ConfigureAwait(false);
                }

                if (!held)
                {
                    outcome = OutboxDispatchOutcome.LeaseLost;
                }
                else if (await PublishAndAdvanceAsync(batch, cursor, tail, cancellationToken).ConfigureAwait(false))
                {
                    published += batch.Count;
                    batches++;
                    cursor = tail;
                }
                else
                {
                    skipped += batch.Count;
                    batches++;
                    cursor = tail;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastDispatchError = exception;
            throw;
        }

        LastDispatchError = null;
        return new OutboxDispatchResult
        {
            Outcome = outcome,
            Published = published,
            Batches = batches,
            Skipped = skipped,
            Cursor = cursor,
        };
    }

    /// <summary>
    /// Renews the lease when <paramref name="renewInterval"/> has elapsed since the last successful
    /// renewal (or acquisition), and only then. Returns whether the lease is still held and the
    /// timestamp to treat as "last renewed" going forward — callers must keep using the returned
    /// timestamp rather than stamping a new one themselves, or a renewal that did not happen would be
    /// mistaken for one that did.
    /// </summary>
    private static async ValueTask<(bool Held, long RenewedAt)> StillHeldAsync(
        IStateLease? lease,
        TimeSpan renewInterval,
        long renewedAt,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (lease is null || Stopwatch.GetElapsedTime(renewedAt) < renewInterval)
        {
            return (true, renewedAt);
        }

        bool renewed = await lease.RenewAsync(ttl, cancellationToken).ConfigureAwait(false);
        return renewed ? (true, Stopwatch.GetTimestamp()) : (false, renewedAt);
    }

    private async ValueTask<bool> PublishAndAdvanceAsync(
        List<StateChangeMessage> batch,
        StateChangeCursor? batchStartCursor,
        StateChangeCursor advanceTo,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sink.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_options.SkipPoisonAfterAttempts is not { } limit)
            {
                throw;
            }

            // Keyed on the batch's START cursor, which is stable across attempts as long as the
            // head of the feed is blocked. Keying on the END cursor instead resets the counter
            // whenever a new record extends the batch between attempts, so the limit is never
            // reached while the store keeps taking writes.
            _poisonAttempts = _poisonCursor == batchStartCursor ? _poisonAttempts + 1 : 1;
            _poisonCursor = batchStartCursor;
            if (_poisonAttempts < limit)
            {
                throw;
            }

            LastSkippedError = exception;
            _poisonCursor = null;
            _poisonAttempts = 0;
            await _cursors.WriteAsync(_options.OutboxId, advanceTo, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _poisonCursor = null;
        _poisonAttempts = 0;
        await _cursors.WriteAsync(_options.OutboxId, advanceTo, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
