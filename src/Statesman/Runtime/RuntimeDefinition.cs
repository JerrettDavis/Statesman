namespace Statesman;

internal sealed record StateLoadOutcome<T>(
    T Value,
    IReadOnlyDictionary<string, string> Metadata,
    StateError? Error);

internal interface IStateRuntimeDefinition
{
    StateDefinitionManifest Manifest { get; }

    Type ValueType { get; }

    IStateHandleInternal CreateHandle(StatesmanRuntime runtime, StateAddress address);

    IStateSnapshot CreateSnapshot(
        StateAddress address,
        StateRecord? record,
        IStateSerializer serializer,
        DateTimeOffset observedAt);
}

internal sealed class StateRuntimeDefinition<T> : IStateRuntimeDefinition
{
    private readonly Func<StateLoadContext, T>? _initial;
    private readonly IReadOnlyList<IStateSource<T>> _sources;
    private readonly IReadOnlyDictionary<string, IStateInteraction<T>> _interactions;
    private readonly IReadOnlyList<StateInvariant<T>> _invariants;
    private readonly IReadOnlyDictionary<int, StateMigration<T>> _migrations;
    private readonly IEqualityComparer<T> _equalityComparer;

    public StateRuntimeDefinition(
        StateDefinitionManifest manifest,
        Func<StateLoadContext, T>? initial,
        IReadOnlyList<IStateSource<T>> sources,
        IReadOnlyList<IStateInteraction<T>> interactions,
        IReadOnlyList<StateInvariant<T>> invariants,
        IReadOnlyList<StateMigration<T>> migrations,
        IEqualityComparer<T> equalityComparer)
    {
        Manifest = manifest;
        _initial = initial;
        _sources = sources;
        _interactions = interactions.ToDictionary(
            interaction => InteractionKey(interaction.CommandType, interaction.Name),
            StringComparer.OrdinalIgnoreCase);
        _invariants = invariants;
        _migrations = migrations.ToDictionary(migration => migration.FromVersion);
        _equalityComparer = equalityComparer;
    }

    public StateDefinitionManifest Manifest { get; }

    public Type ValueType => typeof(T);

    public bool HasLoader => _initial is not null || _sources.Count > 0;

    public bool IsSeedOnly => _initial is not null && _sources.Count == 0;

    public IStateHandleInternal CreateHandle(StatesmanRuntime runtime, StateAddress address) =>
        new StateHandle<T>(runtime, this, address);

    public IStateSnapshot CreateSnapshot(
        StateAddress address,
        StateRecord? record,
        IStateSerializer serializer,
        DateTimeOffset observedAt) =>
        CreateTypedSnapshot(address, record, serializer, observedAt);

    public StateSnapshot<T> CreateTypedSnapshot(
        StateAddress address,
        StateRecord? record,
        IStateSerializer serializer,
        DateTimeOffset observedAt)
    {
        if (record is null)
        {
            return StateSnapshot<T>.Absent(address, observedAt);
        }

        bool hasValue = record.Payload is not null;
        T? value = default;
        if (record.Payload is not null)
        {
            if (record.SchemaVersion == Manifest.SchemaVersion)
            {
                value = serializer.Deserialize<T>(record.Payload);
            }
            else if (_migrations.TryGetValue(record.SchemaVersion, out StateMigration<T>? migration))
            {
                value = migration.Migrate(record.Payload, serializer);
            }
            else
            {
                throw new InvalidOperationException(
                    $"State '{address}' has schema {record.SchemaVersion}, but the declaration expects {Manifest.SchemaVersion} and no migration is registered.");
            }
        }

        StateStatus status = record.Status;
        if (status == StateStatus.Ready && hasValue && record.FreshUntil is DateTimeOffset freshUntil && observedAt > freshUntil)
        {
            status = StateStatus.Stale;
        }

        return new StateSnapshot<T>
        {
            Address = address,
            Revision = record.Revision,
            GlobalPosition = record.GlobalPosition,
            ObservedAt = observedAt,
            OccurredAt = record.OccurredAt,
            Operation = record.Operation,
            Status = status,
            HasValue = hasValue,
            Value = value,
            FreshUntil = record.FreshUntil,
            ServeUntil = record.ServeUntil,
            Error = record.Error,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
        };
    }

    public StateSnapshot<T> Reobserve(StateSnapshot<T> snapshot, DateTimeOffset observedAt)
    {
        StateStatus status = snapshot.Status;
        if ((status == StateStatus.Ready || status == StateStatus.Stale) &&
            snapshot.HasValue &&
            snapshot.FreshUntil is DateTimeOffset freshUntil)
        {
            status = observedAt <= freshUntil ? StateStatus.Ready : StateStatus.Stale;
        }

        return snapshot with { ObservedAt = observedAt, Status = status };
    }

    public async ValueTask<StateLoadOutcome<T>> LoadAsync(
        StateSnapshot<T> current,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = new StateLoadContext(services, current.Address, current, timeProvider);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string Name, Exception Error)>();
        bool hasWorkingValue = current.HasValue;
        bool initialApplied = false;
        int successfulSources = 0;
        T working = current.Value!;
        if (!hasWorkingValue && _initial is not null)
        {
            working = _initial(context);
            hasWorkingValue = true;
            initialApplied = true;
            metadata["statesman.initial.status"] = "applied";
        }

        if (_sources.Count == 0)
        {
            if (!hasWorkingValue)
            {
                throw new StateUnavailableException(current.Address, current.Status, current.Error);
            }

            Validate(working);
            metadata["statesman.load.completeness"] = initialApplied ? "seeded" : "retained";
            return new StateLoadOutcome<T>(working, metadata, null);
        }

        if (Manifest.SourceExecution == StateSourceExecution.Sequential)
        {
            foreach (IStateSource<T> source in _sources)
            {
                try
                {
                    object? part = await source.FetchAsync(context, cancellationToken).ConfigureAwait(false);
                    working = await source.ApplyAsync(working, part, context, cancellationToken).ConfigureAwait(false);
                    hasWorkingValue = true;
                    successfulSources++;
                    metadata[SourceStatusKey(source.Manifest.Name)] = "ready";
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var wrapped = new InvalidOperationException($"State source '{source.Manifest.Name}' failed.", exception);
                    failures.Add((source.Manifest.Name, wrapped));
                    metadata[SourceStatusKey(source.Manifest.Name)] = "faulted";
                    if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll)
                    {
                        throw new AggregateException(
                            $"One or more sources failed while loading '{current.Address}'.",
                            failures.Select(value => value.Error));
                    }
                }
            }
        }
        else
        {
            Task<FetchResult>[] tasks = _sources
                .Select(source => FetchAsync(source, context, cancellationToken))
                .ToArray();
            FetchResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (FetchResult result in results.Where(value => value.Error is not null))
            {
                failures.Add((result.Source.Manifest.Name, result.Error!));
                metadata[SourceStatusKey(result.Source.Manifest.Name)] = "faulted";
            }

            if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll && failures.Count > 0)
            {
                throw new AggregateException(
                    $"One or more sources failed while loading '{current.Address}'.",
                    failures.Select(value => value.Error));
            }

            foreach (FetchResult result in results
                .Where(value => value.Error is null)
                .OrderBy(value => value.Source.Manifest.Order))
            {
                try
                {
                    working = await result.Source.ApplyAsync(
                        working,
                        result.Value,
                        context,
                        cancellationToken).ConfigureAwait(false);
                    hasWorkingValue = true;
                    successfulSources++;
                    metadata[SourceStatusKey(result.Source.Manifest.Name)] = "ready";
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var wrapped = new InvalidOperationException(
                        $"State source '{result.Source.Manifest.Name}' could not project its result.",
                        exception);
                    failures.Add((result.Source.Manifest.Name, wrapped));
                    metadata[SourceStatusKey(result.Source.Manifest.Name)] = "faulted";
                    if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll)
                    {
                        throw new AggregateException(
                            $"One or more sources failed while loading '{current.Address}'.",
                            failures.Select(value => value.Error));
                    }
                }
            }
        }

        if (!hasWorkingValue || (successfulSources == 0 && !initialApplied))
        {
            throw new AggregateException(
                $"No source produced state for '{current.Address}'.",
                failures.Select(value => value.Error));
        }

        Validate(working);
        StateError? partialError = failures.Count == 0
            ? null
            : new StateError(
                "partial-load",
                $"{failures.Count} of {_sources.Count} declared state sources failed.",
                Detail: string.Join(", ", failures.Select(value => value.Name).Order(StringComparer.OrdinalIgnoreCase)),
                IsTransient: true);
        metadata["statesman.load.completeness"] = failures.Count == 0
            ? "complete"
            : successfulSources == 0
                ? "initial-fallback"
                : "partial";
        metadata["statesman.load.sources.ready"] = successfulSources.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["statesman.load.sources.faulted"] = failures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new StateLoadOutcome<T>(working, metadata, partialError);
    }

    public async ValueTask<T> InteractAsync<TCommand>(
        string? name,
        StateSnapshot<T> current,
        TCommand command,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!current.HasValue)
        {
            throw new StateUnavailableException(current.Address, current.Status, current.Error);
        }

        string interactionName = name ?? ResolveDefaultInteractionName(typeof(TCommand));
        string key = InteractionKey(typeof(TCommand), interactionName);
        if (!_interactions.TryGetValue(key, out IStateInteraction<T>? interaction))
        {
            throw new KeyNotFoundException(
                $"No interaction named '{interactionName}' for command '{typeof(TCommand).FullName}' is declared on '{current.Address.Path}'.");
        }

        var context = new StateInteractionContext<T, object>(
            services,
            current.Address,
            current,
            command!,
            timeProvider);
        T next = await interaction.ExecuteAsync(current.RequiredValue, command!, context, cancellationToken).ConfigureAwait(false);
        Validate(next);
        return next;
    }

    public void Validate(T value)
    {
        foreach (StateInvariant<T> invariant in _invariants)
        {
            if (!invariant.Predicate(value))
            {
                throw new StateInvariantException(Manifest.Path, invariant.Message);
            }
        }
    }

    public bool AreEquivalent(T left, T right) => _equalityComparer.Equals(left, right);

    private static string InteractionKey(Type type, string name) => $"{type.AssemblyQualifiedName}|{name}";

    private string ResolveDefaultInteractionName(Type commandType)
    {
        string[] matches = _interactions.Values
            .Where(interaction => interaction.CommandType == commandType)
            .Select(interaction => interaction.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length switch
        {
            0 => commandType.Name,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Command '{commandType.FullName}' has multiple interactions on '{Manifest.Path}'. Specify one of: {string.Join(", ", matches)}."),
        };
    }

    private static string SourceStatusKey(string name)
    {
        char[] buffer = new char[name.Length];
        int length = 0;
        foreach (char character in name)
        {
            buffer[length++] = char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? char.ToLowerInvariant(character)
                : '_';
        }

        return $"statesman.source.{new string(buffer, 0, length)}.status";
    }

    private static async Task<FetchResult> FetchAsync(
        IStateSource<T> source,
        StateLoadContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            object? value = await source.FetchAsync(context, cancellationToken).ConfigureAwait(false);
            return new FetchResult(source, value, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FetchResult(
                source,
                null,
                new InvalidOperationException($"State source '{source.Manifest.Name}' failed.", exception));
        }
    }

    private sealed record FetchResult(IStateSource<T> Source, object? Value, Exception? Error);
}
