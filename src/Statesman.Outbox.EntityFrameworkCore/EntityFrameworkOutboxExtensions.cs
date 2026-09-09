using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Statesman.Outbox;
using Statesman.Outbox.EntityFrameworkCore;

namespace Statesman;

/// <summary>Registers Entity Framework Core cursor storage for a Statesman outbox.</summary>
public static class EntityFrameworkOutboxExtensions
{
    /// <summary>
    /// Registers one outbox whose cursor lives in <typeparamref name="TContext"/>'s
    /// <c>StatesmanOutboxCursors</c> table. Exactly what <c>AddStatesmanOutbox</c> registers — one
    /// hosted worker, one cursor, one lease — with the cursor store bound to Entity Framework Core.
    /// The sink stays the caller's choice, because unlike a Redis connection there is nothing about a
    /// cursor context that implies a destination.
    /// </summary>
    /// <param name="services">The container. <typeparamref name="TContext"/> must already be registered with <c>AddDbContextFactory</c>.</param>
    /// <param name="configure">Configures the outbox. <c>OutboxOptions.StoreName</c> must name a registered store.</param>
    /// <param name="sink">Builds the destination. Resolved once, when the hosted service is first resolved.</param>
    public static IServiceCollection AddStatesmanEntityFrameworkOutbox<TContext>(
        this IServiceCollection services,
        Action<OutboxOptions> configure,
        Func<IServiceProvider, IStateChangeSink> sink)
        where TContext : StatesmanOutboxCursorDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(sink);

        return services.AddStatesmanOutbox(configure, sink, UseEntityFrameworkCursors<TContext>());
    }

    /// <summary>
    /// The <c>cursors</c> argument for <c>AddStatesmanOutbox</c>, resolving
    /// <c>IDbContextFactory&lt;TContext&gt;</c> from the container. Use this when the outbox is
    /// registered directly rather than through
    /// <see cref="AddStatesmanEntityFrameworkOutbox{TContext}"/>.
    /// </summary>
    public static Func<IServiceProvider, IOutboxCursorStore> UseEntityFrameworkCursors<TContext>()
        where TContext : StatesmanOutboxCursorDbContext =>
        static provider => new EntityFrameworkOutboxCursorStore<TContext>(
            provider.GetRequiredService<IDbContextFactory<TContext>>());
}
