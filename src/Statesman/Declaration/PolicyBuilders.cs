namespace Statesman;

public sealed class StateFreshnessBuilder
{
    internal StateFreshnessBuilder(StateFreshnessPolicy policy)
    {
        Policy = policy;
    }

    internal StateFreshnessPolicy Policy { get; private set; }

    public StateFreshnessBuilder FreshFor(TimeSpan duration)
    {
        Policy = Policy with { FreshFor = duration };
        return this;
    }

    public StateFreshnessBuilder ServeStaleFor(TimeSpan duration)
    {
        Policy = Policy with { ServeStaleFor = duration };
        return this;
    }

    public StateFreshnessBuilder StaleWhileRevalidate(bool enabled = true)
    {
        Policy = Policy with { RefreshStaleInBackground = enabled };
        return this;
    }

    public StateFreshnessBuilder NeverExpires()
    {
        Policy = Policy with
        {
            FreshFor = TimeSpan.MaxValue,
            ServeStaleFor = TimeSpan.Zero,
            RefreshStaleInBackground = false,
        };
        return this;
    }
}

public sealed class StateRetentionBuilder
{
    internal StateRetentionBuilder(StateRetentionPolicy policy)
    {
        Policy = policy;
    }

    internal StateRetentionPolicy Policy { get; private set; }

    public StateRetentionBuilder Forever()
    {
        Policy = Policy with { MaxRevisions = null, MaxAge = null, MaxBytes = null };
        return this;
    }

    public StateRetentionBuilder Last(int revisions)
    {
        Policy = Policy with { MaxRevisions = revisions };
        return this;
    }

    public StateRetentionBuilder For(TimeSpan age)
    {
        Policy = Policy with { MaxAge = age };
        return this;
    }

    public StateRetentionBuilder UpToBytes(long bytes)
    {
        Policy = Policy with { MaxBytes = bytes };
        return this;
    }

    public StateRetentionBuilder KeepTombstones(bool enabled = true)
    {
        Policy = Policy with { KeepTombstones = enabled };
        return this;
    }
}

public sealed class StateRefreshBuilder
{
    internal StateRefreshBuilder(StateRefreshPolicy policy)
    {
        Policy = policy;
    }

    internal StateRefreshPolicy Policy { get; private set; }

    public StateRefreshBuilder OnFirstRead(bool enabled = true)
    {
        Policy = Policy with { OnFirstRead = enabled };
        return this;
    }

    public StateRefreshBuilder WhenStale(bool enabled = true)
    {
        Policy = Policy with { WhenStale = enabled };
        return this;
    }

    public StateRefreshBuilder Every(TimeSpan interval)
    {
        Policy = Policy with { Interval = interval };
        return this;
    }

    public StateRefreshBuilder OnSignal(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var signals = new HashSet<string>(Policy.Signals, StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            signals.Add(name.Trim());
        }

        Policy = Policy with { Signals = signals };
        return this;
    }

    public StateRefreshBuilder WarmOnStart(params string[] partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        StatePartition[] warmPartitions = partitions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new StatePartition(value))
            .Distinct()
            .ToArray();

        Policy = Policy with { WarmOnStart = true, WarmPartitions = warmPartitions };
        return this;
    }
}

public sealed class StateDefaultsBuilder
{
    private readonly DeclarationDefaults _defaults;

    internal StateDefaultsBuilder(DeclarationDefaults defaults)
    {
        _defaults = defaults;
    }

    public StateDefaultsBuilder StoreWith(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _defaults.Store = name.Trim();
        return this;
    }

    public StateDefaultsBuilder Freshness(Action<StateFreshnessBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateFreshnessBuilder(_defaults.Freshness);
        configure(builder);
        _defaults.Freshness = builder.Policy;
        return this;
    }

    public StateDefaultsBuilder Retain(Action<StateRetentionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateRetentionBuilder(_defaults.Retention);
        configure(builder);
        _defaults.Retention = builder.Policy;
        return this;
    }

    public StateDefaultsBuilder Refresh(Action<StateRefreshBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateRefreshBuilder(_defaults.Refresh);
        configure(builder);
        _defaults.Refresh = builder.Policy;
        return this;
    }

    public StateDefaultsBuilder KeepLastKnownOnFault(bool enabled = true)
    {
        _defaults.FaultBehavior = enabled
            ? StateFaultBehavior.KeepLastKnown
            : StateFaultBehavior.ReplaceWithFault;
        return this;
    }

    public StateDefaultsBuilder SuppressEquivalentWrites(bool enabled = true)
    {
        _defaults.WriteBehavior = enabled
            ? StateWriteBehavior.SuppressEquivalent
            : StateWriteBehavior.RecordAll;
        return this;
    }
}
