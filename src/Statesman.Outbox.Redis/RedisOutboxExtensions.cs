using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Statesman.Outbox;
using Statesman.Outbox.Redis;

namespace Statesman;

/// <summary>Registers a Statesman outbox that publishes to a Redis stream and keeps its cursor in Redis.</summary>
public static class RedisOutboxExtensions
{
    /// <summary>
    /// Registers one outbox whose sink is a Redis stream and whose cursor lives in a Redis key. The
    /// same connection backs both.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Configures the outbox. <see cref="OutboxOptions.StoreName"/> must name a registered store.</param>
    /// <param name="connection">Supplies the Redis connection. Not owned by the sink or the cursor store.</param>
    /// <param name="configureSink">Optionally configures the stream key and trimming.</param>
    /// <param name="configureCursors">Optionally configures the cursor key namespace.</param>
    public static IServiceCollection AddStatesmanRedisOutbox(
        this IServiceCollection services,
        Action<OutboxOptions> configure,
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        Action<RedisStreamStateChangeSinkOptions>? configureSink = null,
        Action<RedisOutboxCursorStoreOptions>? configureCursors = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connection);

        var sinkOptions = new RedisStreamStateChangeSinkOptions();
        configureSink?.Invoke(sinkOptions);
        var cursorOptions = new RedisOutboxCursorStoreOptions();
        configureCursors?.Invoke(cursorOptions);

        return services.AddStatesmanOutbox(
            configure,
            provider => new RedisStreamStateChangeSink(connection(provider), sinkOptions),
            provider => new RedisOutboxCursorStore(connection(provider), cursorOptions));
    }
}
