namespace Statesman.Testing;

public static class StateSnapshotAssertions
{
    public static IStateSnapshot<T> ShouldBeReady<T>(this IStateSnapshot<T> snapshot)
    {
        if (snapshot.Status != StateStatus.Ready || !snapshot.HasValue)
        {
            throw new StateAssertionException(
                $"Expected '{snapshot.Address}' to be ready with a value, but it was '{snapshot.Status}'.");
        }

        return snapshot;
    }

    public static IStateSnapshot<T> ShouldHaveRevision<T>(this IStateSnapshot<T> snapshot, long revision)
    {
        if (snapshot.Revision != revision)
        {
            throw new StateAssertionException(
                $"Expected '{snapshot.Address}' to be at revision {revision}, but it was {snapshot.Revision}.");
        }

        return snapshot;
    }

    public static IStateSnapshot<T> ShouldEqualValue<T>(this IStateSnapshot<T> snapshot, T expected)
    {
        if (!snapshot.HasValue || !EqualityComparer<T>.Default.Equals(snapshot.Value!, expected))
        {
            throw new StateAssertionException(
                $"Expected '{snapshot.Address}' to contain '{expected}', but it contained '{snapshot.UntypedValue ?? "<absent>"}'.");
        }

        return snapshot;
    }
}

public sealed class StateAssertionException : Exception
{
    public StateAssertionException(string message)
        : base(message)
    {
    }
}
