namespace Statesman.Outbox;

/// <summary>
/// A sink that records everything published to it. Public and shipped in the package — not confined
/// to a test project — because consumers writing their own dispatch tests need it, and because
/// <c>Statesman.Testing</c> referencing the outbox would invert the dependency.
/// </summary>
public sealed class InMemoryStateChangeSink : IStateChangeSink
{
    private readonly List<StateChangeMessage> _published = [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <summary>A snapshot of everything published so far, in the order it was published.</summary>
    public IReadOnlyList<StateChangeMessage> Published
    {
        get
        {
            lock (_gate)
            {
                return _published.ToArray();
            }
        }
    }

    /// <summary>Discards everything recorded so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _published.Clear();
        }
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _published.AddRange(batch);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
