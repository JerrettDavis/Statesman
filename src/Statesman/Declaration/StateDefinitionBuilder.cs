namespace Statesman;

public sealed class StateDefinitionBuilder<T>
{
    private readonly StateDefinitionRegistration<T> _registration;

    internal StateDefinitionBuilder(StateDefinitionRegistration<T> registration)
    {
        _registration = registration;
    }

    public StateDefinitionBuilder<T> Partitioned(bool enabled = true)
    {
        _registration.IsPartitioned = enabled;
        return this;
    }

    public StateDefinitionBuilder<T> Singleton()
    {
        _registration.IsPartitioned = false;
        return this;
    }

    public StateDefinitionBuilder<T> SchemaVersion(int version)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A schema version must be greater than zero.");
        }

        _registration.SchemaVersion = version;
        return this;
    }

    public StateDefinitionBuilder<T> Initial(T value)
    {
        _registration.Initial = _ => value;
        return this;
    }

    public StateDefinitionBuilder<T> Initial(Func<StateLoadContext, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _registration.Initial = factory;
        return this;
    }

    public StateDefinitionBuilder<T> StoreWith(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _registration.Store = name.Trim();
        return this;
    }

    public StateDefinitionBuilder<T> Freshness(Action<StateFreshnessBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateFreshnessBuilder(_registration.Freshness);
        configure(builder);
        _registration.Freshness = builder.Policy;
        return this;
    }

    public StateDefinitionBuilder<T> Retain(Action<StateRetentionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateRetentionBuilder(_registration.Retention);
        configure(builder);
        _registration.Retention = builder.Policy;
        return this;
    }

    public StateDefinitionBuilder<T> Refresh(Action<StateRefreshBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateRefreshBuilder(_registration.Refresh);
        configure(builder);
        _registration.Refresh = builder.Policy;
        return this;
    }

    public StateDefinitionBuilder<T> Load(Action<StateLoadBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new StateLoadBuilder<T>(_registration.Load));
        return this;
    }

    public StateDefinitionBuilder<T> Interact<TCommand>(
        string name,
        Action<StateInteractionBuilder<T, TCommand>> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateInteractionBuilder<T, TCommand>(name.Trim());
        configure(builder);
        _registration.Interactions.Add(builder.Build());
        return this;
    }

    public StateDefinitionBuilder<T> Invariant(string message, Func<T, bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(predicate);
        _registration.Invariants.Add(new StateInvariant<T> { Message = message.Trim(), Predicate = predicate });
        return this;
    }

    public StateDefinitionBuilder<T> CompareWith(IEqualityComparer<T> comparer)
    {
        _registration.EqualityComparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        return this;
    }

    public StateDefinitionBuilder<T> SuppressEquivalentWrites(bool enabled = true)
    {
        _registration.WriteBehavior = enabled
            ? StateWriteBehavior.SuppressEquivalent
            : StateWriteBehavior.RecordAll;
        return this;
    }

    public StateDefinitionBuilder<T> KeepLastKnownOnFault(bool enabled = true)
    {
        _registration.FaultBehavior = enabled
            ? StateFaultBehavior.KeepLastKnown
            : StateFaultBehavior.ReplaceWithFault;
        return this;
    }

    public StateDefinitionBuilder<T> MigrateFrom<TPrevious>(
        int schemaVersion,
        Func<TPrevious, T> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        _registration.Migrations.Add(new StateMigration<T>
        {
            FromVersion = schemaVersion,
            Migrate = (payload, serializer) =>
            {
                TPrevious previous = serializer.Deserialize<TPrevious>(payload.Span)
                    ?? throw new InvalidOperationException(
                        $"Could not deserialize schema {schemaVersion} as '{typeof(TPrevious).FullName}'.");
                return migrate(previous);
            },
        });
        return this;
    }

    public StateDefinitionBuilder<T> Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _registration.Description = description.Trim();
        return this;
    }

    public StateDefinitionBuilder<T> Tag(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _registration.Tags[key.Trim()] = value;
        return this;
    }
}
