using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Statesman.Outbox;

namespace Statesman;

/// <summary>Registers Statesman outboxes in a dependency-injection container.</summary>
public static class StatesmanOutboxExtensions
{
    /// <summary>
    /// Registers one outbox: a hosted worker that polls the named store's change feed and publishes
    /// to <paramref name="sink"/>. Call it once per outbox; each registration gets its own hosted
    /// service, cursor, and lease.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Configures this outbox. <see cref="OutboxOptions.StoreName"/> must name a registered store.</param>
    /// <param name="sink">Builds the destination. Resolved once, when the hosted service is first resolved.</param>
    /// <param name="cursors">Builds the cursor store. Defaults to <see cref="InMemoryOutboxCursorStore"/>, which loses its position on restart — supply a durable one in production.</param>
    public static IServiceCollection AddStatesmanOutbox(
        this IServiceCollection services,
        Action<OutboxOptions> configure,
        Func<IServiceProvider, IStateChangeSink> sink,
        Func<IServiceProvider, IOutboxCursorStore>? cursors = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(sink);

        var options = new OutboxOptions();
        configure(options);
        options.Validate();

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(RegisteredOutboxId) &&
                descriptor.ImplementationInstance is RegisteredOutboxId registered &&
                string.Equals(registered.Value, options.OutboxId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"An outbox with OutboxId '{options.OutboxId}' is already registered in this service collection. " +
                    "Cursors are keyed by OutboxId alone, so two outboxes sharing one id would share one cursor even over different stores. " +
                    $"Set a distinct {nameof(OutboxOptions)}.{nameof(OutboxOptions.OutboxId)} for each outbox.");
            }
        }

        services.AddSingleton(new RegisteredOutboxId(options.OutboxId));

        Func<IServiceProvider, IOutboxCursorStore> cursorFactory =
            cursors ?? (static _ => new InMemoryOutboxCursorStore());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHostedService>(provider => new StatesmanOutboxHostedService(
            CreateDispatcher(provider, options, sink, cursorFactory),
            options,
            provider.GetRequiredService<ILogger<StatesmanOutboxHostedService>>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>
    /// Builds a dispatcher from the container: resolves <see cref="OutboxOptions.StoreName"/> through
    /// <see cref="IStateStoreResolver"/> and, when <see cref="OutboxOptions.Root"/> is set and
    /// <see cref="OutboxOptions.Fingerprint"/> is not, fills the fingerprint in from that root's
    /// manifest. Public so an application that drives <see cref="StateChangeDispatcher.DispatchOnceAsync"/>
    /// itself does not have to re-implement the wiring.
    /// </summary>
    public static StateChangeDispatcher CreateDispatcher(
        IServiceProvider services,
        OutboxOptions options,
        Func<IServiceProvider, IStateChangeSink> sink,
        Func<IServiceProvider, IOutboxCursorStore> cursors)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(cursors);
        options.Validate();

        IStateLedgerStore store = services
            .GetRequiredService<IStateStoreResolver>()
            .Resolve(options.StoreName);

        if (options.Fingerprint is null && options.Root is { } root)
        {
            options.Fingerprint = services
                .GetRequiredService<IStatesmanRegistry>()
                .Get(root)
                .Manifest
                .Fingerprint;
        }

        return new StateChangeDispatcher(store, sink(services), cursors(services), options);
    }

    /// <summary>
    /// A marker registered once per <see cref="AddStatesmanOutbox"/> call, scanned back out of the
    /// container to detect a duplicate <see cref="OutboxOptions.OutboxId"/> before it can share a
    /// cursor with another outbox. Not intended to be resolved.
    /// </summary>
    private sealed class RegisteredOutboxId
    {
        public RegisteredOutboxId(string value) => Value = value;

        public string Value { get; }
    }
}
