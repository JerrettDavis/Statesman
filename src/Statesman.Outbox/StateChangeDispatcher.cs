namespace Statesman.Outbox;

/// <summary>
/// Reads a store's <see cref="IStateChangeFeed"/> from a persisted cursor and publishes every record
/// it reads to an <see cref="IStateChangeSink"/>, at least once.
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
/// <see cref="DispatchOnceAsync"/> is public so one cycle can be driven deterministically without a
/// hosted service and a real timer.
/// </para>
/// </remarks>
public sealed class StateChangeDispatcher
{
    private readonly IStateLedgerStore _store;
    private readonly IStateChangeFeed _feed;
    private readonly IStateChangeSink _sink;
    private readonly IOutboxCursorStore _cursors;
    private readonly OutboxOptions _options;

    /// <summary>Creates a dispatcher over one store, sink, and cursor store.</summary>
    /// <exception cref="NotSupportedException"><paramref name="store"/> does not implement <see cref="IStateChangeFeed"/>.</exception>
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

        _store = store;
        _feed = feed;
        _sink = sink;
        _cursors = cursors;
        _options = options;
    }

    /// <summary>The configured <see cref="OutboxOptions.OutboxId"/>, which keys the cursor.</summary>
    public string OutboxId => _options.OutboxId;

    /// <summary>The exception the last cycle failed with, or <see langword="null"/> after a cycle that completed.</summary>
    public Exception? LastDispatchError { get; private set; }

    /// <summary>Runs exactly one dispatch cycle: read the cursor, drain the feed, publish, advance.</summary>
    public async ValueTask<OutboxDispatchResult> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        StateChangeCursor? cursor = await _cursors.ReadAsync(_options.OutboxId, cancellationToken).ConfigureAwait(false);
        var batch = new List<StateChangeMessage>(_options.BatchSize);
        int published = 0;
        int batches = 0;

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

                await PublishAndAdvanceAsync(batch, pending.Value, cancellationToken).ConfigureAwait(false);
                published += batch.Count;
                batches++;
                cursor = pending;
                batch.Clear();
            }

            if (batch.Count > 0 && pending is { } tail)
            {
                await PublishAndAdvanceAsync(batch, tail, cancellationToken).ConfigureAwait(false);
                published += batch.Count;
                batches++;
                cursor = tail;
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
            Outcome = OutboxDispatchOutcome.Completed,
            Published = published,
            Batches = batches,
            Cursor = cursor,
        };
    }

    private async ValueTask PublishAndAdvanceAsync(
        List<StateChangeMessage> batch,
        StateChangeCursor cursor,
        CancellationToken cancellationToken)
    {
        await _sink.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        await _cursors.WriteAsync(_options.OutboxId, cursor, cancellationToken).ConfigureAwait(false);
    }
}
