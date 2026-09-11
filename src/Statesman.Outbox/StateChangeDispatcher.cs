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
/// at-least-once delivery of every record <em>the feed yields</em>, and the feed is lossless within
/// retention on every provider: a consumer that resumes from the cursor of the last record it
/// accepted is never skipped past a record. What the feed does not cover is retention — a record
/// leaves the change feed exactly when its history record leaves the store, on every provider, so
/// with any non-default retention policy a record can be gone milliseconds after the write and
/// before the outbox reaches it — plus one provider-specific
/// residual: on the filesystem and in-memory providers, neither of which implements
/// <see cref="IStateLeaseProvider"/>, an outbox must run with
/// <see cref="OutboxOptions.RequireLease"/> <c>= false</c>, admitting the cursor race described
/// below. So a complete configuration for feed losslessness is <b>any provider with
/// <see cref="StateRetentionPolicy.KeepAll"/></b>, with that one residual still standing — the
/// lease residual for a filesystem- or in-memory-backed outbox. See <c>docs/guides/outbox.md</c>.
/// </para>
/// <para>
/// Two dispatchers running unleased over one store do not merely double-publish — they race the
/// cursor store, and a cursor write can move past records the other never published. That is why
/// <see cref="OutboxOptions.RequireLease"/> defaults to true and why every
/// <see cref="IOutboxCursorStore"/> write is monotonic.
/// </para>
/// <para>
/// <see cref="DispatchOnceAsync"/> is public so one cycle can be driven deterministically without
/// a hosted service and a real timer. This dispatcher is a <b>persistent leader</b>: it holds its
/// lease across cycles and renews it on <see cref="OutboxOptions.LeaseRenewInterval"/>, so lease
/// traffic scales with time rather than with the number of cycles. That makes disposal an
/// obligation — a caller driving cycles directly must dispose this dispatcher (or call
/// <see cref="ReleaseLeaseAsync"/>) so a standby replica can take over promptly instead of
/// waiting out <see cref="OutboxOptions.LeaseTtl"/>. One consequence worth naming: one replica now
/// does all the work until it stops or dies, where a per-cycle acquire let replicas share load by
/// accident.
/// </para>
/// </remarks>
public sealed class StateChangeDispatcher : IAsyncDisposable
{
    private readonly IStateLedgerStore _store;
    private readonly IStateChangeFeed _feed;
    private readonly IStateLeaseProvider? _leases;
    private readonly IStateChangeSink _sink;
    private readonly IOutboxCursorStore _cursors;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private StateChangeCursor? _poisonCursor;
    private int _poisonAttempts;
    private IStateLease? _lease;
    private long _leaseRenewedAt;

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
        : this(store, sink, cursors, options, TimeProvider.System)
    {
    }

    /// <summary>Creates a dispatcher over one store, sink, and cursor store, on a given clock.</summary>
    /// <remarks>
    /// <para>
    /// The clock measures lease-renewal cadence and nothing else.
    /// <see cref="TimeProvider.System"/> and <c>Stopwatch</c> are the same counter at the same
    /// frequency, so the four-argument constructor computes every value it computed before ROADMAP
    /// 0.3 Phase 13.
    /// </para>
    /// <para>
    /// An overload rather than a defaulted parameter on the four-argument constructor: adding one
    /// there is source-compatible but binary-breaking, so a consumer assembly compiled against an
    /// earlier version would throw <see cref="MissingMethodException"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// <paramref name="store"/> does not implement <see cref="IStateChangeFeed"/>, or does not
    /// implement <see cref="IStateLeaseProvider"/> while <see cref="OutboxOptions.RequireLease"/> is
    /// set.
    /// </exception>
    public StateChangeDispatcher(
        IStateLedgerStore store,
        IStateChangeSink sink,
        IOutboxCursorStore cursors,
        OutboxOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
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
        _timeProvider = timeProvider;
        store.TryGetCapability(out IStateChangeNotifier? notifier);
        ChangeNotifier = notifier;

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

    /// <summary>The sink this dispatcher publishes to. The hosted service disposes it, and releases this dispatcher's lease; a caller driving <see cref="DispatchOnceAsync"/> directly owns both instead, by disposing this dispatcher and the sink.</summary>
    public IStateChangeSink Sink => _sink;

    /// <summary>
    /// The store's change-notification capability, or <see langword="null"/> when it has none. The
    /// hosted worker subscribes to it so a change is dispatched without waiting out
    /// <see cref="OutboxOptions.PollInterval"/>. It is a latency hint only: no record is read from
    /// it, and no cursor is ever advanced from one.
    /// </summary>
    public IStateChangeNotifier? ChangeNotifier { get; }

    /// <summary>Runs exactly one dispatch cycle: make sure the lease is held, read the cursor, drain the feed, publish, advance.</summary>
    /// <remarks>
    /// Not safe to call concurrently on the same instance: the poison-attempt counter, the held lease
    /// and its renewal timestamp are unsynchronized instance state. The lease is held <b>across</b>
    /// calls — this dispatcher is a persistent leader, so a caller driving cycles directly must
    /// dispose this dispatcher (or call <see cref="ReleaseLeaseAsync"/>) when it stops.
    /// </remarks>
    public async ValueTask<OutboxDispatchResult> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_leases is not null && !await EnsureLeaseAsync(cancellationToken).ConfigureAwait(false))
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

        OutboxDispatchResult result = await DrainAsync(_lease, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == OutboxDispatchOutcome.LeaseLost)
        {
            // The drain already stopped before the batch it could not cover. The handle is worthless
            // now, so drop it here rather than carrying it into the next cycle, where EnsureLeaseAsync
            // would spend a renewal discovering the same thing.
            await DropLeaseAsync().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Releases the held lease, if any. Idempotent, so the hosting layer can call it from a
    /// <c>finally</c>, from a failed cycle's catch, and from disposal without counting.
    /// </summary>
    public ValueTask ReleaseLeaseAsync() => DropLeaseAsync();

    /// <summary>
    /// Releases the held lease. Does <b>not</b> dispose <see cref="Sink"/>: the hosted service owns
    /// that, and a caller driving <see cref="DispatchOnceAsync"/> directly owns it instead.
    /// </summary>
    public async ValueTask DisposeAsync() => await ReleaseLeaseAsync().ConfigureAwait(false);

    /// <summary>
    /// Whether this dispatcher holds the lease at the moment the cycle begins. A held lease is
    /// renewed on <see cref="OutboxOptions.LeaseRenewInterval"/> rather than re-acquired, which is
    /// what stops lease traffic scaling with the number of cycles (and so, under hint-driven
    /// dispatch, with write volume). A renewal the provider refuses means the handle is worthless, so
    /// it is dropped and exactly one acquire is attempted in the same cycle.
    /// </summary>
    private async ValueTask<bool> EnsureLeaseAsync(CancellationToken cancellationToken)
    {
        if (_lease is not null)
        {
            (bool held, long renewedAt) = await StillHeldAsync(
                _lease,
                _options.EffectiveLeaseRenewInterval,
                _leaseRenewedAt,
                _options.LeaseTtl,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            if (held)
            {
                _leaseRenewedAt = renewedAt;
                return true;
            }

            await DropLeaseAsync().ConfigureAwait(false);
        }

        _lease = await _leases!.AcquireAsync(LeaseId, _options.LeaseTtl, cancellationToken).ConfigureAwait(false);
        _leaseRenewedAt = _timeProvider.GetTimestamp();
        return _lease is not null;
    }

    private async ValueTask DropLeaseAsync()
    {
        IStateLease? lease = _lease;
        _lease = null;
        if (lease is not null)
        {
            // Release is "delete only if the token still matches" on both providers that implement
            // leases, so disposing a handle another holder already took is a safe no-op.
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<OutboxDispatchResult> DrainAsync(IStateLease? lease, CancellationToken cancellationToken)
    {
        StateChangeCursor? cursor = await _cursors.ReadAsync(_options.OutboxId, cancellationToken).ConfigureAwait(false);
        TimeSpan renewInterval = _options.EffectiveLeaseRenewInterval;
        // Seeded from the dispatcher's field, NOT from a fresh timestamp: the lease is held across
        // cycles, so the renewal interval is measured from the last actual renewal (or acquisition),
        // not from the start of this cycle. Stamping a fresh timestamp here would re-stamp the clock
        // on every cycle, so the interval would never be satisfied and the lease would silently
        // lapse at LeaseTtl.
        long renewedAt = _leaseRenewedAt;
        var batch = new List<StateChangeMessage>(_options.BatchSize);
        var outcome = OutboxDispatchOutcome.Completed;
        int published = 0;
        int batches = 0;
        int skipped = 0;

        try
        {
            // One read per page, bounded by BatchSize, until a page yields nothing. This is what
            // makes OutboxOptions.BatchSize a single knob: it bounds what the provider materializes
            // as well as what is published. Termination is "a page came back empty", not "the
            // enumerator ended" -- a short page only means the provider reached its tail as of that
            // read, so the loop reads once more to find out.
            var readOptions = new StateChangeReadOptions { Take = _options.BatchSize };
            while (true)
            {
                int pageRecords = 0;
                StateChangeCursor? pending = null;
                await foreach (StateChangeEnvelope envelope in _feed.ReadAsync(cursor, readOptions, cancellationToken).ConfigureAwait(false))
                {
                    pageRecords++;
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
                        bool stillHeld;
                        (stillHeld, renewedAt) = await StillHeldAsync(lease, renewInterval, renewedAt, _options.LeaseTtl, _timeProvider, cancellationToken).ConfigureAwait(false);
                        if (!stillHeld)
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

                if (outcome != OutboxDispatchOutcome.Completed)
                {
                    break;
                }

                if (batch.Count > 0 && pending is { } tail)
                {
                    bool held = true;
                    if (batches > 0)
                    {
                        (held, renewedAt) = await StillHeldAsync(lease, renewInterval, renewedAt, _options.LeaseTtl, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    if (!held)
                    {
                        outcome = OutboxDispatchOutcome.LeaseLost;
                        break;
                    }

                    if (await PublishAndAdvanceAsync(batch, cursor, tail, cancellationToken).ConfigureAwait(false))
                    {
                        published += batch.Count;
                    }
                    else
                    {
                        skipped += batch.Count;
                    }

                    batches++;
                    cursor = tail;
                    batch.Clear();
                }

                if (pageRecords == 0)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _leaseRenewedAt = renewedAt;
            LastDispatchError = exception;
            throw;
        }

        _leaseRenewedAt = renewedAt;
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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (lease is null || timeProvider.GetElapsedTime(renewedAt) < renewInterval)
        {
            return (true, renewedAt);
        }

        bool renewed = await lease.RenewAsync(ttl, cancellationToken).ConfigureAwait(false);
        return renewed ? (true, timeProvider.GetTimestamp()) : (false, renewedAt);
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
