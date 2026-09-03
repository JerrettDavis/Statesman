namespace Statesman;

public sealed record StateSourceManifest(
    string Name,
    string ServiceType,
    string ResultType,
    int Order,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StateInteractionManifest(
    string Name,
    string CommandType,
    string Description);

public sealed record StateDefinitionManifest
{
    public required StatePath Path { get; init; }

    public required string ValueType { get; init; }

    public required int SchemaVersion { get; init; }

    public bool IsPartitioned { get; init; }

    public required string Store { get; init; }

    public required StateFreshnessPolicy Freshness { get; init; }

    public required StateRetentionPolicy Retention { get; init; }

    public required StateRefreshPolicy Refresh { get; init; }

    public StateSourceExecution SourceExecution { get; init; }

    public StateSourceFailureMode SourceFailureMode { get; init; }

    public StateFaultBehavior FaultBehavior { get; init; }

    public StateWriteBehavior WriteBehavior { get; init; }

    public IReadOnlyList<StateSourceManifest> Sources { get; init; } = Array.Empty<StateSourceManifest>();

    public IReadOnlyList<StateInteractionManifest> Interactions { get; init; } = Array.Empty<StateInteractionManifest>();

    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Description { get; init; }
}

public sealed record StateContainerManifest
{
    public required StatePath Path { get; init; }

    public StateContainerIsolation Isolation { get; init; }

    public IReadOnlyList<StatePath> States { get; init; } = Array.Empty<StatePath>();

    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Description { get; init; }
}

public sealed record StatesmanManifest
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Fingerprint { get; init; }

    public IReadOnlyList<StateContainerManifest> Containers { get; init; } = Array.Empty<StateContainerManifest>();

    public IReadOnlyList<StateDefinitionManifest> States { get; init; } = Array.Empty<StateDefinitionManifest>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
