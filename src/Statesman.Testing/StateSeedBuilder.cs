namespace Statesman.Testing;

/// <summary>
/// Fluent, strongly typed scenario setup for tests that should declare their starting state in code.
/// </summary>
public sealed class StateSeedBuilder
{
    private readonly IStatesman _statesman;
    private readonly List<Func<CancellationToken, ValueTask>> _steps = new();
    private string _source = "test-seed";
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);

    internal StateSeedBuilder(IStatesman statesman)
    {
        _statesman = statesman;
    }

    public StateSeedBuilder Source(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _source = source;
        return this;
    }

    public StateSeedBuilder Metadata(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _metadata[key] = value;
        return this;
    }

    public StateSeedBuilder State<T>(StateKey<T> key, T value, StatePartition? partition = null)
    {
        StatePartition resolved = partition ?? StatePartition.Default;
        _steps.Add(async cancellationToken =>
        {
            await _statesman.State(key, resolved).SetAsync(
                value,
                WriteOptions(),
                cancellationToken).ConfigureAwait(false);
        });
        return this;
    }

    public StateSeedBuilder Invalidated<T>(
        StateKey<T> key,
        T value,
        string? reason = null,
        StatePartition? partition = null)
    {
        StatePartition resolved = partition ?? StatePartition.Default;
        _steps.Add(async cancellationToken =>
        {
            IState<T> state = _statesman.State(key, resolved);
            await state.SetAsync(value, WriteOptions(), cancellationToken).ConfigureAwait(false);
            await state.InvalidateAsync(reason ?? "test-seed", WriteOptions(), cancellationToken).ConfigureAwait(false);
        });
        return this;
    }

    public StateSeedBuilder Cleared<T>(StateKey<T> key, StatePartition? partition = null)
    {
        StatePartition resolved = partition ?? StatePartition.Default;
        _steps.Add(async cancellationToken =>
        {
            await _statesman.State(key, resolved).ClearAsync(
                "test-seed",
                WriteOptions(),
                cancellationToken).ConfigureAwait(false);
        });
        return this;
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken = default)
    {
        foreach (Func<CancellationToken, ValueTask> step in _steps)
        {
            await step(cancellationToken).ConfigureAwait(false);
        }
    }

    private StateWriteOptions WriteOptions() => new()
    {
        Source = _source,
        Metadata = new Dictionary<string, string>(_metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["statesman.seed"] = "true",
        },
    };
}

public static class StateSeedExtensions
{
    public static StateSeedBuilder Seed(this IStatesman statesman)
    {
        ArgumentNullException.ThrowIfNull(statesman);
        return new StateSeedBuilder(statesman);
    }
}
