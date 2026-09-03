namespace Statesman;

public interface IStateSnapshot
{
    StateAddress Address { get; }

    Type ValueType { get; }

    long Revision { get; }

    long GlobalPosition { get; }

    DateTimeOffset ObservedAt { get; }

    DateTimeOffset? OccurredAt { get; }

    StateOperation? Operation { get; }

    StateStatus Status { get; }

    bool HasValue { get; }

    bool IsFresh { get; }

    bool IsStale { get; }

    DateTimeOffset? FreshUntil { get; }

    DateTimeOffset? ServeUntil { get; }

    object? UntypedValue { get; }

    StateError? Error { get; }

    IReadOnlyDictionary<string, string> Metadata { get; }
}

public interface IStateSnapshot<out T> : IStateSnapshot
{
    T? Value { get; }

    T RequiredValue { get; }
}

public sealed record StateSnapshot<T> : IStateSnapshot<T>
{
    public required StateAddress Address { get; init; }

    public Type ValueType => typeof(T);

    public long Revision { get; init; }

    public long GlobalPosition { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public StateOperation? Operation { get; init; }

    public StateStatus Status { get; init; } = StateStatus.Absent;

    public bool HasValue { get; init; }

    public T? Value { get; init; }

    object? IStateSnapshot.UntypedValue => Value;

    public DateTimeOffset? FreshUntil { get; init; }

    public DateTimeOffset? ServeUntil { get; init; }

    public StateError? Error { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsFresh => HasValue && FreshUntil is not null && ObservedAt <= FreshUntil;

    public bool IsStale => HasValue && !IsFresh;

    public T RequiredValue => HasValue
        ? Value!
        : throw new StateUnavailableException(Address, Status, Error);

    public static StateSnapshot<T> Absent(StateAddress address, DateTimeOffset observedAt) => new()
    {
        Address = address,
        ObservedAt = observedAt,
        Status = StateStatus.Absent,
        HasValue = false,
    };
}

public sealed record StateChange(
    IStateSnapshot Previous,
    IStateSnapshot Current,
    StateOperation Operation);

public sealed record StateChange<T>(
    IStateSnapshot<T> Previous,
    IStateSnapshot<T> Current,
    StateOperation Operation);

public sealed record StateSnapshotSet(
    string Root,
    long CapturePosition,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<StateAddress, IStateSnapshot> Snapshots);
