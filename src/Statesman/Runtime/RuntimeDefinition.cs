namespace Statesman;

internal sealed record StateLoadOutcome<T>(
    T Value,
    IReadOnlyDictionary<string, string> Metadata,
    StateError? Error,
    StateLoadReport Report);

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
    // The key StateHandle reads the per-source reports back from after a load throws. Internal, and
    // an Data entry rather than an exception subclass: RecordFaultAsync stamps the exception's type
    // name into the fault snapshot's StateError.Type, which is persisted in the ledger and documented
    // as operator-facing, so a subclass would silently change a stored field on every RequireAll
    // fault. Pre-Phase-18 addendum decision 85.
    internal const string SourceReportsExceptionDataKey = "statesman.load.sources";

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
        var sourceReports = new Dictionary<string, StateSourceLoadReport>(StringComparer.Ordinal);

        // The runtime's own clock, never Stopwatch. ManualTimeProvider overrides GetTimestamp and
        // TimestampFrequency (ROADMAP 0.3 Phase 13), so GetElapsedTime measures exact VIRTUAL time and
        // a load-diagnostics test asserts an exact equality rather than a real-time bound.
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        long startedTimestamp = timeProvider.GetTimestamp();
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
            string emptyCompleteness = initialApplied ? "seeded" : "retained";
            metadata["statesman.load.completeness"] = emptyCompleteness;
            return new StateLoadOutcome<T>(
                working,
                metadata,
                null,
                CompleteLoad(
                    current.Address, metadata, timeProvider, startedAt, startedTimestamp,
                    emptyCompleteness, sourcesReady: 0, sourcesFaulted: 0, sourceReports));
        }

        if (Manifest.SourceExecution == StateSourceExecution.Sequential)
        {
            foreach (IStateSource<T> source in _sources)
            {
                long sourceStarted = timeProvider.GetTimestamp();
                try
                {
                    object? part = await source.FetchAsync(context, cancellationToken).ConfigureAwait(false);
                    working = await source.ApplyAsync(working, part, context, cancellationToken).ConfigureAwait(false);
                    hasWorkingValue = true;
                    successfulSources++;
                    metadata[SourceStatusKey(source.Manifest.Name)] = "ready";
                    sourceReports[source.Manifest.Name] =
                        ReadySource(source.Manifest.Name, timeProvider.GetElapsedTime(sourceStarted));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var wrapped = new InvalidOperationException($"State source '{source.Manifest.Name}' failed.", exception);
                    failures.Add((source.Manifest.Name, wrapped));
                    metadata[SourceStatusKey(source.Manifest.Name)] = "faulted";
                    sourceReports[source.Manifest.Name] = FaultedSource(
                        source.Manifest.Name, timeProvider.GetElapsedTime(sourceStarted), exception);
                    if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll)
                    {
                        throw LoadFailed(
                            $"One or more sources failed while loading '{current.Address}'.",
                            failures,
                            sourceReports);
                    }
                }
            }
        }
        else
        {
            Task<FetchResult>[] tasks = _sources
                .Select(source => FetchAsync(source, context, timeProvider, cancellationToken))
                .ToArray();
            FetchResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (FetchResult result in results.Where(value => value.Error is not null))
            {
                failures.Add((result.Source.Manifest.Name, result.Error!));
                metadata[SourceStatusKey(result.Source.Manifest.Name)] = "faulted";
                sourceReports[result.Source.Manifest.Name] =
                    FaultedSource(result.Source.Manifest.Name, result.Elapsed, result.Cause!);
            }

            if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll && failures.Count > 0)
            {
                throw LoadFailed(
                    $"One or more sources failed while loading '{current.Address}'.",
                    failures,
                    sourceReports);
            }

            foreach (FetchResult result in results
                .Where(value => value.Error is null)
                .OrderBy(value => value.Source.Manifest.Order))
            {
                long applyStarted = timeProvider.GetTimestamp();
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
                    sourceReports[result.Source.Manifest.Name] = ReadySource(
                        result.Source.Manifest.Name,
                        result.Elapsed + timeProvider.GetElapsedTime(applyStarted));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var wrapped = new InvalidOperationException(
                        $"State source '{result.Source.Manifest.Name}' could not project its result.",
                        exception);
                    failures.Add((result.Source.Manifest.Name, wrapped));
                    metadata[SourceStatusKey(result.Source.Manifest.Name)] = "faulted";
                    sourceReports[result.Source.Manifest.Name] = FaultedSource(
                        result.Source.Manifest.Name,
                        result.Elapsed + timeProvider.GetElapsedTime(applyStarted),
                        exception);
                    if (Manifest.SourceFailureMode == StateSourceFailureMode.RequireAll)
                    {
                        throw LoadFailed(
                            $"One or more sources failed while loading '{current.Address}'.",
                            failures,
                            sourceReports);
                    }
                }
            }
        }

        if (!hasWorkingValue || (successfulSources == 0 && !initialApplied))
        {
            throw LoadFailed(
                $"No source produced state for '{current.Address}'.",
                failures,
                sourceReports);
        }

        Validate(working);
        StateError? partialError = failures.Count == 0
            ? null
            : new StateError(
                "partial-load",
                $"{failures.Count} of {_sources.Count} declared state sources failed.",
                Detail: string.Join(", ", failures.Select(value => value.Name).Order(StringComparer.OrdinalIgnoreCase)),
                IsTransient: true);
        string completeness = failures.Count == 0
            ? "complete"
            : successfulSources == 0
                ? "initial-fallback"
                : "partial";
        metadata["statesman.load.completeness"] = completeness;
        metadata["statesman.load.sources.ready"] = successfulSources.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["statesman.load.sources.faulted"] = failures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new StateLoadOutcome<T>(
            working,
            metadata,
            partialError,
            CompleteLoad(
                current.Address, metadata, timeProvider, startedAt, startedTimestamp,
                completeness, successfulSources, failures.Count, sourceReports));
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

    private static StateSourceLoadReport ReadySource(string name, TimeSpan elapsed) => new()
    {
        Name = name,
        Status = StateSourceLoadStatus.Ready,
        Elapsed = elapsed,
    };

    private static StateSourceLoadReport FaultedSource(string name, TimeSpan elapsed, Exception cause) => new()
    {
        Name = name,
        Status = StateSourceLoadStatus.Faulted,
        Elapsed = elapsed,
        ExceptionType = cause.GetType().FullName,
        ExceptionMessage = cause.Message,
    };

    // Every throw site builds its exception here, so the reports can never be attached at three of
    // the four. sourceReports is already fully populated at each site; StateHandle's per-source
    // histogram loop runs only AFTER LoadAsync returns, which a throw never does, so without this the
    // one mode that exists because every source matters records no per-source timing anywhere.
    private static AggregateException LoadFailed(
        string message,
        List<(string Name, Exception Error)> failures,
        Dictionary<string, StateSourceLoadReport> sourceReports)
    {
        var exception = new AggregateException(message, failures.Select(value => value.Error));
        exception.Data[SourceReportsExceptionDataKey] = sourceReports.Values.ToArray();
        return exception;
    }

    // Stamps the three bounded timing keys on the record's metadata AND builds the structured report,
    // in one place so the two can never disagree about how long a load took. Why the per-source
    // breakdown is deliberately NOT among them is stated once, on StateSourceLoadReport's remarks in
    // Statesman.Abstractions -- pre-Phase-17 addendum decision 68. Do not restate it here; two copies
    // of one rationale is how they drift.
    //
    // CompletedAt is StartedAt plus the measured elapsed rather than a second GetUtcNow call, so the
    // three values are arithmetically consistent by construction on any clock, virtual or real.
    private StateLoadReport CompleteLoad(
        StateAddress address,
        Dictionary<string, string> metadata,
        TimeProvider timeProvider,
        DateTimeOffset startedAt,
        long startedTimestamp,
        string completeness,
        int sourcesReady,
        int sourcesFaulted,
        Dictionary<string, StateSourceLoadReport> sourceReports)
    {
        TimeSpan elapsed = timeProvider.GetElapsedTime(startedTimestamp);
        DateTimeOffset completedAt = startedAt + elapsed;
        metadata["statesman.load.duration.ms"] =
            elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["statesman.load.started"] =
            startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        metadata["statesman.load.completed"] =
            completedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return new StateLoadReport
        {
            Address = address,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Elapsed = elapsed,
            Completeness = completeness,
            SourcesReady = sourcesReady,
            SourcesFaulted = sourcesFaulted,
            Sources = [.. _sources
                .Select(source => sourceReports.GetValueOrDefault(source.Manifest.Name))
                .OfType<StateSourceLoadReport>()],
        };
    }

    private static async Task<FetchResult> FetchAsync(
        IStateSource<T> source,
        StateLoadContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        long started = timeProvider.GetTimestamp();
        try
        {
            object? value = await source.FetchAsync(context, cancellationToken).ConfigureAwait(false);
            return new FetchResult(source, value, null, null, timeProvider.GetElapsedTime(started));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new FetchResult(
                source,
                null,
                new InvalidOperationException($"State source '{source.Manifest.Name}' failed.", exception),
                exception,
                timeProvider.GetElapsedTime(started));
        }
    }

    // Error is the wrapped exception the load's failure list carries; Cause is the source's own, which
    // is what a report should name — an operator reading "InvalidOperationException: State source 'x'
    // failed." learns nothing the source's own type and message do not say better.
    private sealed record FetchResult(
        IStateSource<T> Source, object? Value, Exception? Error, Exception? Cause, TimeSpan Elapsed);
}
