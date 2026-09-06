namespace Statesman.Outbox.Tests;

/// <summary>A sink that rejects its first <c>failures</c> batches, then records like the in-memory sink.</summary>
internal sealed class FailingStateChangeSink : IStateChangeSink
{
    private readonly int _failures;
    private readonly List<StateChangeMessage> _published = [];
    private int _attempts;

    public FailingStateChangeSink(int failures) => _failures = failures;

    public string Name => "failing";

    public int Attempts => _attempts;

    public IReadOnlyList<StateChangeMessage> Published => _published;

    public ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
    {
        _attempts++;
        if (_attempts <= _failures)
        {
            throw new InvalidOperationException($"Sink rejected batch attempt {_attempts}.");
        }

        _published.AddRange(batch);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
