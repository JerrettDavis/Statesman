namespace Statesman.Outbox;

/// <summary>
/// A destination the outbox publishes ledger changes to. Implementations are called from one
/// dispatcher cycle at a time and must not be assumed thread-safe by callers other than the
/// dispatcher.
/// </summary>
/// <remarks>
/// The contract is a batch, never a single record: a single-record signature forces a round trip per
/// record and forbids pipelining, while a batch can always be implemented one at a time.
/// <see cref="PublishAsync"/> must throw when the batch was not accepted — the dispatcher persists
/// the cursor only after it returns, so returning normally is what tells the outbox the records are
/// safe to move past. Delivery is at-least-once: the same batch can be re-delivered after a crash,
/// so a sink that can deduplicate should key on <see cref="StateChangeMessage.MessageId"/>.
/// </remarks>
public interface IStateChangeSink : IAsyncDisposable
{
    /// <summary>A short name for this sink, used in log messages and errors.</summary>
    string Name { get; }

    /// <summary>Publishes one batch in ascending <see cref="StateChangeMessage.GlobalPosition"/> order. Throws if the batch was not accepted.</summary>
    ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default);
}
