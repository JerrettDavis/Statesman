using System.Collections.ObjectModel;

namespace Statesman;

internal sealed class DeclarationDefaults
{
    public string Store { get; set; } = "memory";

    public StateFreshnessPolicy Freshness { get; set; } = StateFreshnessPolicy.Default;

    public StateRetentionPolicy Retention { get; set; } = StateRetentionPolicy.KeepAll;

    public StateRefreshPolicy Refresh { get; set; } = StateRefreshPolicy.None;

    public StateFaultBehavior FaultBehavior { get; set; } = StateFaultBehavior.KeepLastKnown;

    public StateWriteBehavior WriteBehavior { get; set; } = StateWriteBehavior.RecordAll;

    public DeclarationDefaults Clone() => new()
    {
        Store = Store,
        Freshness = Freshness with { },
        Retention = Retention with { },
        Refresh = Refresh with
        {
            WarmPartitions = Refresh.WarmPartitions.ToArray(),
            Signals = new HashSet<string>(Refresh.Signals, StringComparer.OrdinalIgnoreCase),
        },
        FaultBehavior = FaultBehavior,
        WriteBehavior = WriteBehavior,
    };
}

internal sealed class ContainerRegistration
{
    public required StatePath Path { get; init; }

    public StateContainerIsolation Isolation { get; set; }

    public string? Description { get; set; }

    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<StatePath> States { get; } = new();
}

internal sealed class StateInvariant<T>
{
    public required string Message { get; init; }

    public required Func<T, bool> Predicate { get; init; }
}

internal sealed class StateMigration<T>
{
    public required int FromVersion { get; init; }

    public required Func<ReadOnlyMemory<byte>, IStateSerializer, T> Migrate { get; init; }
}

internal interface IStateSource<T>
{
    StateSourceManifest Manifest { get; }

    ValueTask<object?> FetchAsync(StateLoadContext context, CancellationToken cancellationToken);

    ValueTask<T> ApplyAsync(T current, object? value, StateLoadContext context, CancellationToken cancellationToken);
}

internal sealed class StateSource<T, TPart> : IStateSource<T>
{
    private readonly Func<IServiceProvider, StateLoadContext, CancellationToken, ValueTask<TPart>> _fetch;
    private readonly Func<T, TPart, StateLoadContext, CancellationToken, ValueTask<T>> _apply;

    public StateSource(
        StateSourceManifest manifest,
        Func<IServiceProvider, StateLoadContext, CancellationToken, ValueTask<TPart>> fetch,
        Func<T, TPart, StateLoadContext, CancellationToken, ValueTask<T>> apply)
    {
        Manifest = manifest;
        _fetch = fetch;
        _apply = apply;
    }

    public StateSourceManifest Manifest { get; }

    public async ValueTask<object?> FetchAsync(StateLoadContext context, CancellationToken cancellationToken) =>
        await _fetch(context.Services, context, cancellationToken).ConfigureAwait(false);

    public async ValueTask<T> ApplyAsync(
        T current,
        object? value,
        StateLoadContext context,
        CancellationToken cancellationToken)
    {
        if (value is not TPart part)
        {
            if (value is null && default(TPart) is null)
            {
                return await _apply(current, (TPart)value!, context, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Source '{Manifest.Name}' produced '{value?.GetType().FullName ?? "null"}' instead of '{typeof(TPart).FullName}'.");
        }

        return await _apply(current, part, context, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class StateLoadConfiguration<T>
{
    public List<IStateSource<T>> Sources { get; } = new();

    public StateSourceExecution Execution { get; set; } = StateSourceExecution.Sequential;

    public StateSourceFailureMode FailureMode { get; set; } = StateSourceFailureMode.RequireAll;
}

internal interface IStateInteraction<T>
{
    string Name { get; }

    Type CommandType { get; }

    StateInteractionManifest Manifest { get; }

    ValueTask<T> ExecuteAsync(
        T current,
        object command,
        StateInteractionContext<T, object> context,
        CancellationToken cancellationToken);
}

internal sealed class StateInteraction<T, TCommand> : IStateInteraction<T>
{
    private readonly IReadOnlyList<Func<T, TCommand, StateInteractionContext<T, TCommand>, CancellationToken, ValueTask<string?>>> _requirements;
    private readonly Func<T, TCommand, StateInteractionContext<T, TCommand>, CancellationToken, ValueTask<T>> _reducer;

    public StateInteraction(
        string name,
        string description,
        IReadOnlyList<Func<T, TCommand, StateInteractionContext<T, TCommand>, CancellationToken, ValueTask<string?>>> requirements,
        Func<T, TCommand, StateInteractionContext<T, TCommand>, CancellationToken, ValueTask<T>> reducer)
    {
        Name = name;
        _requirements = requirements;
        _reducer = reducer;
        Manifest = new StateInteractionManifest(name, StateTypeNames.Stable(typeof(TCommand)), description);
    }

    public string Name { get; }

    public Type CommandType => typeof(TCommand);

    public StateInteractionManifest Manifest { get; }

    public async ValueTask<T> ExecuteAsync(
        T current,
        object command,
        StateInteractionContext<T, object> context,
        CancellationToken cancellationToken)
    {
        if (command is not TCommand typedCommand)
        {
            throw new ArgumentException(
                $"Interaction '{Name}' expects '{typeof(TCommand).FullName}', not '{command?.GetType().FullName ?? "null"}'.",
                nameof(command));
        }

        var typedContext = new StateInteractionContext<T, TCommand>(
            context.Services,
            context.Address,
            context.Current,
            typedCommand,
            context.TimeProvider);

        var reasons = new List<string>();
        foreach (Func<T, TCommand, StateInteractionContext<T, TCommand>, CancellationToken, ValueTask<string?>> requirement in _requirements)
        {
            string? reason = await requirement(current, typedCommand, typedContext, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                reasons.Add(reason);
            }
        }

        if (reasons.Count > 0)
        {
            throw new StateInteractionRejectedException(context.Address, Name, reasons);
        }

        return await _reducer(current, typedCommand, typedContext, cancellationToken).ConfigureAwait(false);
    }
}

internal interface IStateDefinitionRegistration
{
    IStateRuntimeDefinition Freeze();
}

internal sealed class StateDefinitionRegistration<T> : IStateDefinitionRegistration
{
    public StateDefinitionRegistration(StatePath path, DeclarationDefaults defaults)
    {
        Path = path;
        Store = defaults.Store;
        Freshness = defaults.Freshness with { };
        Retention = defaults.Retention with { };
        Refresh = defaults.Refresh with
        {
            WarmPartitions = defaults.Refresh.WarmPartitions.ToArray(),
            Signals = new HashSet<string>(defaults.Refresh.Signals, StringComparer.OrdinalIgnoreCase),
        };
        FaultBehavior = defaults.FaultBehavior;
        WriteBehavior = defaults.WriteBehavior;
    }

    public StatePath Path { get; }

    public int SchemaVersion { get; set; } = 1;

    public bool IsPartitioned { get; set; }

    public string Store { get; set; }

    public StateFreshnessPolicy Freshness { get; set; }

    public StateRetentionPolicy Retention { get; set; }

    public StateRefreshPolicy Refresh { get; set; }

    public StateFaultBehavior FaultBehavior { get; set; }

    public StateWriteBehavior WriteBehavior { get; set; }

    public string? Description { get; set; }

    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<StateLoadContext, T>? Initial { get; set; }

    public StateLoadConfiguration<T> Load { get; } = new();

    public List<IStateInteraction<T>> Interactions { get; } = new();

    public List<StateInvariant<T>> Invariants { get; } = new();

    public List<StateMigration<T>> Migrations { get; } = new();

    public IEqualityComparer<T> EqualityComparer { get; set; } = EqualityComparer<T>.Default;

    public IStateRuntimeDefinition Freeze()
    {
        Freshness.Validate();
        Retention.Validate();
        Refresh.Validate();

        if (Path.IsRoot)
        {
            throw new StateDeclarationException("A state cannot use the root path.");
        }

        if (string.IsNullOrWhiteSpace(Store))
        {
            throw new StateDeclarationException($"State '{Path}' has no ledger store.");
        }

        string[] duplicateSources = Load.Sources
            .GroupBy(source => source.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateSources.Length > 0)
        {
            throw new StateDeclarationException(
                $"State '{Path}' declares duplicate sources: {string.Join(", ", duplicateSources)}.");
        }

        string[] duplicateInteractions = Interactions
            .GroupBy(interaction => $"{interaction.CommandType.FullName}:{interaction.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateInteractions.Length > 0)
        {
            throw new StateDeclarationException(
                $"State '{Path}' declares duplicate interactions: {string.Join(", ", duplicateInteractions)}.");
        }

        int[] duplicateMigrations = Migrations
            .GroupBy(migration => migration.FromVersion)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateMigrations.Length > 0)
        {
            throw new StateDeclarationException(
                $"State '{Path}' declares duplicate migrations from versions: {string.Join(", ", duplicateMigrations)}.");
        }

        if (Migrations.Any(migration => migration.FromVersion <= 0 || migration.FromVersion >= SchemaVersion))
        {
            throw new StateDeclarationException(
                $"State '{Path}' can only migrate from positive schema versions lower than {SchemaVersion}.");
        }

        StateRefreshPolicy normalizedRefresh = Refresh with
        {
            WarmPartitions = Refresh.WarmPartitions
                .OrderBy(partition => partition.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Signals = new SortedSet<string>(Refresh.Signals, StringComparer.OrdinalIgnoreCase),
        };

        var manifest = new StateDefinitionManifest
        {
            Path = Path,
            ValueType = StateTypeNames.Stable(typeof(T)),
            SchemaVersion = SchemaVersion,
            IsPartitioned = IsPartitioned,
            Store = Store,
            Freshness = Freshness,
            Retention = Retention,
            Refresh = normalizedRefresh,
            SourceExecution = Load.Execution,
            SourceFailureMode = Load.FailureMode,
            FaultBehavior = FaultBehavior,
            WriteBehavior = WriteBehavior,
            Sources = Load.Sources.Select(source => source.Manifest).OrderBy(source => source.Order).ToArray(),
            Interactions = Interactions
                .Select(interaction => interaction.Manifest)
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.CommandType, StringComparer.Ordinal)
                .ToArray(),
            Tags = StatesmanCollections.ReadOnlySorted(Tags),
            Description = Description,
        };

        return new StateRuntimeDefinition<T>(
            manifest,
            Initial,
            Load.Sources.ToArray(),
            Interactions.ToArray(),
            Invariants.ToArray(),
            Migrations.ToArray(),
            EqualityComparer);
    }
}

internal static class StatesmanCollections
{
    public static IReadOnlyDictionary<string, string> ReadOnlySorted(IEnumerable<KeyValuePair<string, string>> values)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in values)
        {
            sorted[pair.Key] = pair.Value;
        }

        return new ReadOnlyDictionary<string, string>(sorted);
    }
}


internal static class StateTypeNames
{
    public static string Stable(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsArray)
        {
            return $"{Stable(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (type.IsByRef)
        {
            return $"{Stable(type.GetElementType()!)}&";
        }

        if (type.IsPointer)
        {
            return $"{Stable(type.GetElementType()!)}*";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        Type definition = type.GetGenericTypeDefinition();
        string name = RemoveGenericArity(definition.FullName ?? definition.Name);
        string arguments = string.Join(",", type.GetGenericArguments().Select(Stable));
        return $"{name}<{arguments}>";
    }

    private static string RemoveGenericArity(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '`')
            {
                output.Append(value[index]);
                continue;
            }

            while (index + 1 < value.Length && char.IsDigit(value[index + 1]))
            {
                index++;
            }
        }

        return output.ToString();
    }
}
