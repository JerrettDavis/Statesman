using System.Reflection;
using System.Text;
using StackExchange.Redis;
using Statesman.TestHelpers;

namespace Statesman.Redis.Tests;

/// <summary>
/// A <see cref="DispatchProxy"/> that forwards every member to <see cref="Inner"/> unless
/// <see cref="Handler"/> claims the call. <see cref="IDatabase"/> and
/// <see cref="IConnectionMultiplexer"/> together declare several hundred members; hand-written
/// stubs are not an option and only one member of each needs intercepting. Public because
/// <see cref="DispatchProxy.Create{T, TProxy}"/> requires a public parameterless type.
/// </summary>
public class ForwardingDispatchProxy : DispatchProxy
{
    /// <summary>The real object every unclaimed call is forwarded to.</summary>
    public object? Inner { get; set; }

    /// <summary>Claims a call, or declines it so it is forwarded unchanged.</summary>
    public Func<MethodInfo, object?[]?, (bool Handled, object? Result)>? Handler { get; set; }

    /// <summary>Dispatches one intercepted call.</summary>
    /// <param name="targetMethod">The interface method being called.</param>
    /// <param name="args">Its arguments.</param>
    /// <returns>The handler's answer, or the forwarded result.</returns>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        (bool handled, object? result) = Handler?.Invoke(targetMethod, args) ?? (false, null);
        return handled ? result : targetMethod.Invoke(Inner, args);
    }
}

public sealed class RedisImportConcurrencyTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("STATESMAN_TEST_REDIS");

    private const string SkipReason =
        "STATESMAN_TEST_REDIS is not set; skipping tests that require a live Redis instance.";

    [Fact]
    public async Task Two_interleaved_same_revision_imports_leave_exactly_one_feed_member()
    {
        // The hazard addendum decision 50 documented and declined to fix, and the fact that closes
        // it. Two imports of the SAME revision each read the history member they are about to
        // replace and each remove that member, by its own bytes, from the change feed. The second
        // import never learns about the member the first one added, so history ends with one member
        // and the change feed ends with two: a record in the feed whose history entry is gone.
        //
        // The interleaving is forced, not raced. A DispatchProxy over IDatabase pauses the first
        // import immediately after its stale-member read returns, on a TaskCompletionSource rather
        // than a delay; the second import then runs to completion against the same server through an
        // unproxied store; only then is the first released. There is no sleep and no timing
        // assumption anywhere in this fact.
        //
        // The pause point is the window a condition-guarded MULTI/EXEC import has between its read
        // and its write. Once ImportAsync is one server-side script that window does not exist:
        // ImportAsync issues no sorted-set read at all, the gate never trips, and the two imports
        // serialize on the server. That absence IS the fix, which is why this fact's Task.WhenAny
        // below is written to proceed whether the gate trips or the first import simply finishes.
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ConnectionString), SkipReason);

        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString!);
        string name = $"import-race-{Guid.NewGuid():N}";
        var address = new StateAddress("app", "import/race", StatePartition.Default);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int tripped = 0;

        IDatabase real = connection.GetDatabase();
        IDatabase gatedDatabase = DispatchProxy.Create<IDatabase, ForwardingDispatchProxy>();
        var databaseProxy = (ForwardingDispatchProxy)gatedDatabase;
        databaseProxy.Inner = real;
        databaseProxy.Handler = (method, args) =>
        {
            if (method.Name != nameof(IDatabase.SortedSetRangeByScoreAsync) ||
                Interlocked.Exchange(ref tripped, 1) != 0)
            {
                return (false, null);
            }

            var inner = (Task<RedisValue[]>)method.Invoke(real, args)!;
            return (true, PauseAsync(inner));
        };

        IConnectionMultiplexer gatedConnection =
            DispatchProxy.Create<IConnectionMultiplexer, ForwardingDispatchProxy>();
        var connectionProxy = (ForwardingDispatchProxy)gatedConnection;
        connectionProxy.Inner = connection;
        connectionProxy.Handler = (method, _) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? (true, gatedDatabase) : (false, null);

        await using var gatedStore = new RedisStateLedgerStore(name, gatedConnection, RedisTestLayout.Options());
        await using var plainStore = new RedisStateLedgerStore(name, connection, RedisTestLayout.Options());

        // The member both imports are about to replace. Without it the first import's stale-member
        // read returns nothing and there is no member for the second import to orphan.
        await plainStore.ImportAsync(Record(address, revision: 1, position: 10, "seed"), TestContext.Current.CancellationToken);

        Task first = gatedStore.ImportAsync(
            Record(address, revision: 1, position: 20, "first"), TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAny(reached.Task, first);

        await plainStore.ImportAsync(
            Record(address, revision: 1, position: 30, "second"), TestContext.Current.CancellationToken);

        release.SetResult();
        await first;

        // One address, one revision, three imports of it: the change feed must hold exactly one
        // member, whichever of the two wrote last. Two members means one of them has no history
        // entry, which is the orphan this fact exists to refuse.
        long feedMembers = await real.SortedSetLengthAsync($"{RedisTestLayout.Scope(name)}:changes");
        Assert.Equal(1, feedMembers);

        StateRecord? head = await plainStore.ReadLatestAsync(address, TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Equal(1, head.Revision);

        async Task<RedisValue[]> PauseAsync(Task<RedisValue[]> inner)
        {
            RedisValue[] value = await inner.ConfigureAwait(false);
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return value;
        }
    }

    private static StateRecord Record(StateAddress address, long revision, long position, string value) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
