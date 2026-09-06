namespace Statesman.Outbox.Tests;

/// <summary>Records published messages and completes <see cref="Reached"/> once the expected count have arrived.</summary>
internal sealed class SignalingStateChangeSink : IStateChangeSink
{
    private readonly int _expected;
    private readonly int _failuresFirst;
    private readonly object _gate = new();
    private readonly List<StateChangeMessage> _published = [];
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;

    public SignalingStateChangeSink(int expected, int failuresFirst = 0)
    {
        _expected = expected;
        _failuresFirst = failuresFirst;
    }

    public string Name => "signaling";

    public Task Reached => _reached.Task;

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

    public ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
    {
        int attempt = Interlocked.Increment(ref _attempts);
        if (attempt <= _failuresFirst)
        {
            throw new InvalidOperationException($"Sink rejected batch attempt {attempt}.");
        }

        lock (_gate)
        {
            _published.AddRange(batch);
            if (_published.Count >= _expected)
            {
                _reached.TrySetResult();
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
