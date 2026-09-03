using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Statesman;

public static class StatesmanServiceCollectionExtensions
{
    public static StatesmanServiceBuilder AddStatesman(
        this IServiceCollection services,
        StatesmanDeclaration declaration,
        Action<StatesmanServiceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(declaration);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IStateSerializer, JsonStateSerializer>();
        services.TryAddSingleton<IStateStoreResolver, DependencyInjectionStateStoreResolver>();
        services.TryAddSingleton<IStatesmanRegistry, StatesmanRegistry>();

        var builder = new StatesmanServiceBuilder(services);
        builder.UseInMemoryStore();
        configure?.Invoke(builder);

        services.AddSingleton<IStatesman>(provider => declaration.CreateRuntime(
            provider,
            provider.GetRequiredService<IStateStoreResolver>(),
            provider.GetRequiredService<IStateSerializer>(),
            provider.GetRequiredService<TimeProvider>()));
        return builder;
    }
}
