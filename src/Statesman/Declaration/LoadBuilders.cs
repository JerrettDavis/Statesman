namespace Statesman;

public sealed class StateLoadBuilder<TState>
{
    private readonly StateLoadConfiguration<TState> _configuration;

    internal StateLoadBuilder(StateLoadConfiguration<TState> configuration)
    {
        _configuration = configuration;
    }

    public StateLoadBuilder<TState> From<TService>(
        string name,
        Func<TService, StateLoadContext, CancellationToken, ValueTask<TState>> load)
        where TService : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(load);
        name = name.Trim();
        int order = _configuration.Sources.Count;
        var manifest = new StateSourceManifest(
            name,
            StateTypeNames.Stable(typeof(TService)),
            StateTypeNames.Stable(typeof(TState)),
            order,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        _configuration.Sources.Add(new StateSource<TState, TState>(
            manifest,
            async (services, context, cancellationToken) =>
            {
                TService service = (TService)(services.GetService(typeof(TService))
                    ?? throw new InvalidOperationException($"No service is registered for '{typeof(TService).FullName}'."));
                return await load(service, context, cancellationToken).ConfigureAwait(false);
            },
            static (_, value, _, _) => ValueTask.FromResult(value)));
        return this;
    }

    public StateSourceBuilder<TState, TPart> From<TService, TPart>(
        string name,
        Func<TService, StateLoadContext, CancellationToken, ValueTask<TPart>> load)
        where TService : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(load);
        return new StateSourceBuilder<TState, TPart>(
            this,
            _configuration,
            name.Trim(),
            typeof(TService),
            async (services, context, cancellationToken) =>
            {
                TService service = (TService)(services.GetService(typeof(TService))
                    ?? throw new InvalidOperationException($"No service is registered for '{typeof(TService).FullName}'."));
                return await load(service, context, cancellationToken).ConfigureAwait(false);
            });
    }

    public StateLoadBuilder<TState> InParallel()
    {
        _configuration.Execution = StateSourceExecution.Parallel;
        return this;
    }

    public StateLoadBuilder<TState> Sequentially()
    {
        _configuration.Execution = StateSourceExecution.Sequential;
        return this;
    }

    public StateLoadBuilder<TState> RequireAll()
    {
        _configuration.FailureMode = StateSourceFailureMode.RequireAll;
        return this;
    }

    public StateLoadBuilder<TState> BestEffort()
    {
        _configuration.FailureMode = StateSourceFailureMode.BestEffort;
        return this;
    }
}

public sealed class StateSourceBuilder<TState, TPart>
{
    private readonly StateLoadBuilder<TState> _parent;
    private readonly StateLoadConfiguration<TState> _configuration;
    private readonly string _name;
    private readonly Type _serviceType;
    private readonly Func<IServiceProvider, StateLoadContext, CancellationToken, ValueTask<TPart>> _load;
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    internal StateSourceBuilder(
        StateLoadBuilder<TState> parent,
        StateLoadConfiguration<TState> configuration,
        string name,
        Type serviceType,
        Func<IServiceProvider, StateLoadContext, CancellationToken, ValueTask<TPart>> load)
    {
        _parent = parent;
        _configuration = configuration;
        _name = name;
        _serviceType = serviceType;
        _load = load;
    }

    public StateSourceBuilder<TState, TPart> Tag(string key, string value)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _metadata[key.Trim()] = value;
        return this;
    }

    public StateLoadBuilder<TState> Into(Func<TState, TPart, StateLoadContext, TState> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return IntoAsync((state, part, context, _) => ValueTask.FromResult(apply(state, part, context)));
    }

    public StateLoadBuilder<TState> IntoAsync(
        Func<TState, TPart, StateLoadContext, CancellationToken, ValueTask<TState>> apply)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(apply);
        int order = _configuration.Sources.Count;
        var manifest = new StateSourceManifest(
            _name,
            StateTypeNames.Stable(_serviceType),
            StateTypeNames.Stable(typeof(TPart)),
            order,
            StatesmanCollections.ReadOnlySorted(_metadata));
        _configuration.Sources.Add(new StateSource<TState, TPart>(manifest, _load, apply));
        _completed = true;
        return _parent;
    }

    private void EnsureOpen()
    {
        if (_completed)
        {
            throw new InvalidOperationException($"Source '{_name}' has already been composed into the state.");
        }
    }
}
