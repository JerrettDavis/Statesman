using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using StackExchange.Redis;

namespace Statesman.Redis.Tests;

/// <summary>
/// Builds and faults StackExchange.Redis's sealed <see cref="ChannelMessageQueue"/>, the one
/// concrete type <see cref="ISubscriber.SubscribeAsync(RedisChannel, CommandFlags)"/> hands back.
/// Its only constructor is internal, takes <c>(ref RedisChannel, RedisSubscriber)</c> and accepts a
/// <see langword="null"/> parent; <c>MarkCompleted(Exception)</c> faults the underlying channel and
/// the exception surfaces from <c>MoveNextAsync</c> unwrapped. Measured against
/// StackExchange.Redis 3.2.1.
/// </summary>
internal static class ReadLoopQueue
{
    private static readonly Type QueueType = typeof(ChannelMessageQueue);

    public static ChannelMessageQueue New(string name)
    {
        ConstructorInfo constructor = QueueType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        var channel = new RedisChannel(name, RedisChannel.PatternMode.Literal);
        return (ChannelMessageQueue)constructor.Invoke([channel, null]);
    }

    public static void Fault(ChannelMessageQueue queue, Exception error)
    {
        MethodInfo mark = QueueType.GetMethod(
            "MarkCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Exception)],
            modifiers: null)!;
        mark.Invoke(queue, [error]);
    }

    /// <summary>
    /// True once the store has reached <c>MoveNextAsync</c>, which is exactly when the underlying
    /// channel has a blocked reader. This is what makes the fact deterministic instead of a sleep.
    /// </summary>
    public static bool HasReader(ChannelMessageQueue queue)
    {
        object channel = QueueType
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!;
        object reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        return !((ValueTask<bool>)reader.GetType()
            .GetMethod("WaitToReadAsync")!
            .Invoke(reader, [CancellationToken.None])!).IsCompleted;
    }
}

/// <summary>
/// A <see cref="DispatchProxy"/> that answers a named handful of members and refuses the rest. Two
/// interfaces of 74 and 22 members would otherwise need roughly ninety hand-written stubs, and the
/// store needs four of them. Public because <see cref="DispatchProxy.Create{T, TProxy}"/> requires
/// a public parameterless type.
/// </summary>
public class ReadLoopDispatchProxy : DispatchProxy
{
    /// <summary>Answers a call, or declines it so the default handling below applies.</summary>
    public Func<MethodInfo, object?[]?, (bool Handled, object? Result)>? Handler { get; set; }

    /// <summary>Dispatches one intercepted call.</summary>
    /// <param name="targetMethod">The interface method being called.</param>
    /// <param name="args">Its arguments.</param>
    /// <returns>The handler's answer, or a benign default for disposal and <see cref="ValueTask"/> members.</returns>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new NotSupportedException("no method");
        }

        (bool handled, object? result) = Handler?.Invoke(targetMethod, args) ?? (false, null);
        if (handled)
        {
            return result;
        }

        if (targetMethod.Name is "Dispose" or "Close")
        {
            return null;
        }

        if (targetMethod.ReturnType == typeof(ValueTask))
        {
            return default(ValueTask);
        }

        throw new NotSupportedException(targetMethod.Name);
    }
}

internal static class ReadLoopFakes
{
    /// <summary>
    /// A multiplexer whose subscriber hands back <paramref name="queue"/>. <c>GetDatabase</c>
    /// answers null deliberately: the store's constructor calls it, and nothing on the notifier
    /// path ever touches the result.
    /// </summary>
    /// <param name="queue">The queue every subscription resolves to.</param>
    /// <returns>A multiplexer answering only what the notifier path calls.</returns>
    public static IConnectionMultiplexer Multiplexer(ChannelMessageQueue queue)
    {
        ISubscriber subscriber = Subscriber(queue);
        object proxy = DispatchProxy.Create<IConnectionMultiplexer, ReadLoopDispatchProxy>()!;
        ((ReadLoopDispatchProxy)proxy).Handler = (method, arguments) => method.Name switch
        {
            "GetSubscriber" => (true, subscriber),
            "GetDatabase" => (true, null),
            _ => (false, null),
        };
        return (IConnectionMultiplexer)proxy;
    }

    private static ISubscriber Subscriber(ChannelMessageQueue queue)
    {
        object proxy = DispatchProxy.Create<ISubscriber, ReadLoopDispatchProxy>()!;
        ((ReadLoopDispatchProxy)proxy).Handler = (method, arguments) => method.Name switch
        {
            "SubscribeAsync" => (true, Task.FromResult(queue)),
            _ => (false, null),
        };
        return (ISubscriber)proxy;
    }
}

public sealed class RedisReadLoopFailureTests
{
    /// <summary>
    /// The read loop's guard swallows four widened exception types only when
    /// <c>linked.IsCancellationRequested</c> confirms the store's own disposal cancelled them. This
    /// pins that operand: neither token is cancelled here, so all four must reach the caller rather
    /// than end the sequence with a silent clean <see langword="false"/>. Removing
    /// <c>&amp;&amp; linked.IsCancellationRequested</c> from the filter leaves
    /// <c>!cancellationToken.IsCancellationRequested</c>, which is true here, and all four rows
    /// become a clean end.
    /// </summary>
    /// <param name="kind">Which widened exception type to inject at the enumerator.</param>
    [Theory]
    [InlineData("redis")]
    [InlineData("io")]
    [InlineData("socket")]
    [InlineData("objectdisposed")]
    public async Task A_genuine_failure_at_the_read_loop_with_no_disposal_reaches_the_caller(string kind)
    {
        ChannelMessageQueue queue = ReadLoopQueue.New("statesman:read-loop:changes");
        await using var store = new RedisStateLedgerStore("read-loop", ReadLoopFakes.Multiplexer(queue));

        // The five-argument overload, because RedisConnectionException(ConnectionFailureType, string)
        // is [Obsolete] and TreatWarningsAsErrors turns that into error CS0618.
        Exception injected = kind switch
        {
            "redis" => new RedisConnectionException(
                ConnectionFailureType.SocketFailure, CommandFlags.None, "injected", null, CommandStatus.Unknown),
            "io" => new IOException("injected", new SocketException(995)),
            "socket" => new SocketException(995),
            _ => new ObjectDisposedException("multiplexer"),
        };

        var observed = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (StateChangeNotification _ in store.SubscribeAsync())
                {
                }

                observed.TrySetResult("clean-end");
            }
            catch (Exception exception)
            {
                observed.TrySetResult(exception.GetType().FullName!);
            }
        });

        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 5000 && !ReadLoopQueue.HasReader(queue))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        ReadLoopQueue.Fault(queue, injected);
        Task completed = await Task.WhenAny(observed.Task, Task.Delay(10000, TestContext.Current.CancellationToken));
        string result = completed == observed.Task ? await observed.Task : "TIMEOUT";

        Assert.NotEqual("clean-end", result);
        Assert.NotEqual("TIMEOUT", result);
    }
}
