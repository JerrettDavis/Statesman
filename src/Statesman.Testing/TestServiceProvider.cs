namespace Statesman.Testing;

public sealed class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();
    private readonly IServiceProvider? _fallback;

    public TestServiceProvider(IServiceProvider? fallback = null)
    {
        _fallback = fallback;
    }

    public TestServiceProvider Add<T>(T service)
        where T : notnull
    {
        _services[typeof(T)] = service;
        return this;
    }

    public object? GetService(Type serviceType) =>
        serviceType == typeof(IServiceProvider)
            ? this
            : _services.TryGetValue(serviceType, out object? service)
                ? service
                : _fallback?.GetService(serviceType);
}
