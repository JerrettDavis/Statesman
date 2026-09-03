using Microsoft.Extensions.DependencyInjection;

namespace Statesman;

public interface IStateStoreFactory
{
    string Name { get; }

    IStateLedgerStore Create(IServiceProvider services);
}

public sealed class StatesmanServiceBuilder
{
    internal StatesmanServiceBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    public StatesmanServiceBuilder UseStore(
        string name,
        Func<IServiceProvider, IStateLedgerStore> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton<IStateStoreFactory>(new DelegateStateStoreFactory(name, factory));
        return this;
    }

    public StatesmanServiceBuilder UseInMemoryStore(string name = "memory") =>
        UseStore(name, services => new InMemoryStateLedgerStore(
            name,
            services.GetService<TimeProvider>() ?? TimeProvider.System));

    private sealed class DelegateStateStoreFactory : IStateStoreFactory
    {
        private readonly Func<IServiceProvider, IStateLedgerStore> _factory;

        public DelegateStateStoreFactory(string name, Func<IServiceProvider, IStateLedgerStore> factory)
        {
            Name = name;
            _factory = factory;
        }

        public string Name { get; }

        public IStateLedgerStore Create(IServiceProvider services) => _factory(services);
    }
}
