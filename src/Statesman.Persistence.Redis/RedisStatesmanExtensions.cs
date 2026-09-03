using StackExchange.Redis;

namespace Statesman;

public static class RedisStatesmanExtensions
{
    public static StatesmanServiceBuilder UseRedisStore(
        this StatesmanServiceBuilder builder,
        string name,
        IConnectionMultiplexer connection,
        RedisStateLedgerStoreOptions? options = null) =>
        builder.UseStore(name, services => new RedisStateLedgerStore(
            name,
            connection,
            options,
            services.GetService(typeof(TimeProvider)) as TimeProvider));


    public static StatesmanServiceBuilder UseRedisStore(
        this StatesmanServiceBuilder builder,
        string name,
        IConnectionMultiplexer connection,
        Action<RedisStateLedgerStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisStateLedgerStoreOptions();
        configure(options);
        return builder.UseRedisStore(name, connection, options);
    }

    public static StatesmanServiceBuilder UseRedisStore(
        this StatesmanServiceBuilder builder,
        string name,
        Func<IServiceProvider, IConnectionMultiplexer> connection,
        RedisStateLedgerStoreOptions? options = null) =>
        builder.UseStore(name, services => new RedisStateLedgerStore(
            name,
            connection(services),
            options,
            services.GetService(typeof(TimeProvider)) as TimeProvider));
}
