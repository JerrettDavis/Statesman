using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman.Testing;

/// <summary>
/// Portable logical state captured for deterministic integration and end-to-end test setup.
/// Applying a fixture creates new ledger revisions in the target runtime; it does not impersonate
/// the original ledger positions or timestamps.
/// </summary>
public sealed record StateFixture
{
    public string Format { get; init; } = "statesman.fixture/v1";

    public required string Root { get; init; }

    public string? ManifestFingerprint { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    public IReadOnlyList<StateFixtureEntry> States { get; init; } = Array.Empty<StateFixtureEntry>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record StateFixtureEntry
{
    public required string Path { get; init; }

    public string Partition { get; init; } = "default";

    public required string ValueType { get; init; }

    public string? AssemblyQualifiedValueType { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StateStatus Status { get; init; }

    public bool HasValue { get; init; }

    public string? ValueJson { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record StateFixtureApplyOptions
{
    /// <summary>Reject fixtures captured from a different Statesman root.</summary>
    public bool RequireMatchingRoot { get; init; } = true;

    /// <summary>Reject fixtures captured against a different declaration fingerprint.</summary>
    public bool RequireMatchingManifest { get; init; } = true;

    /// <summary>Source recorded on the new revisions created while seeding.</summary>
    public string Source { get; init; } = "test-fixture";

    /// <summary>
    /// When true, fixture entries for paths no longer present in the declaration fail the seed.
    /// When false, they are skipped to aid staged migrations.
    /// </summary>
    public bool FailOnUnknownState { get; init; } = true;
}

public static class StateFixtureExtensions
{
    private static readonly JsonSerializerOptions DefaultJson = CreateJsonOptions();

    public static StateFixture ToFixture(
        this StateSnapshotSet snapshots,
        StatesmanManifest manifest,
        IReadOnlyDictionary<string, string>? metadata = null,
        JsonSerializerOptions? json = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(manifest);
        JsonSerializerOptions options = json ?? DefaultJson;

        StateFixtureEntry[] entries = snapshots.Snapshots.Values
            .OrderBy(snapshot => snapshot.Address.Canonical, StringComparer.Ordinal)
            .Select(snapshot => new StateFixtureEntry
            {
                Path = snapshot.Address.Path.Value,
                Partition = snapshot.Address.Partition.Value,
                ValueType = snapshot.ValueType.FullName ?? snapshot.ValueType.Name,
                AssemblyQualifiedValueType = snapshot.ValueType.AssemblyQualifiedName,
                Status = snapshot.Status,
                HasValue = snapshot.HasValue,
                ValueJson = snapshot.HasValue
                    ? JsonSerializer.Serialize(snapshot.UntypedValue, snapshot.ValueType, options)
                    : null,
                Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase),
            })
            .ToArray();

        return new StateFixture
        {
            Root = snapshots.Root,
            ManifestFingerprint = manifest.Fingerprint,
            CapturedAt = snapshots.CapturedAt,
            States = entries,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    public static async ValueTask<StateFixture> CaptureFixtureAsync(
        this IStatesman statesman,
        IEnumerable<StateReference> references,
        StateReadOptions? options = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        JsonSerializerOptions? json = null,
        CancellationToken cancellationToken = default)
    {
        StateSnapshotSet snapshots = await statesman.CaptureAsync(references, options, cancellationToken)
            .ConfigureAwait(false);
        return snapshots.ToFixture(statesman.Manifest, metadata, json);
    }

    public static async ValueTask ApplyFixtureAsync(
        this IStatesman statesman,
        StateFixture fixture,
        StateFixtureApplyOptions? options = null,
        JsonSerializerOptions? json = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statesman);
        ArgumentNullException.ThrowIfNull(fixture);
        StateFixtureApplyOptions apply = options ?? new StateFixtureApplyOptions();
        JsonSerializerOptions serializer = json ?? DefaultJson;

        if (apply.RequireMatchingRoot && !string.Equals(fixture.Root, statesman.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new StateFixtureException(
                $"Fixture root '{fixture.Root}' cannot seed Statesman root '{statesman.Id}'.");
        }

        if (apply.RequireMatchingManifest &&
            !string.IsNullOrWhiteSpace(fixture.ManifestFingerprint) &&
            !string.Equals(fixture.ManifestFingerprint, statesman.Manifest.Fingerprint, StringComparison.Ordinal))
        {
            throw new StateFixtureException(
                $"Fixture manifest '{fixture.ManifestFingerprint}' does not match '{statesman.Manifest.Fingerprint}'.");
        }

        Dictionary<StatePath, StateDefinitionManifest> declared = statesman.Manifest.States
            .ToDictionary(state => state.Path);

        foreach (StateFixtureEntry entry in fixture.States)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatePath path = new(entry.Path);
            if (!declared.TryGetValue(path, out StateDefinitionManifest? definition))
            {
                if (apply.FailOnUnknownState)
                {
                    throw new StateFixtureException($"Fixture state '{entry.Path}' is not declared by root '{statesman.Id}'.");
                }

                continue;
            }

            StatePartition partition = string.Equals(entry.Partition, "default", StringComparison.Ordinal)
                ? StatePartition.Default
                : new StatePartition(entry.Partition);
            StateReference reference = new(path, partition);
            var write = new StateWriteOptions
            {
                Source = apply.Source,
                Metadata = MergeMetadata(entry.Metadata, fixture.Metadata),
            };

            if (entry.Status == StateStatus.Cleared || (!entry.HasValue && entry.Status != StateStatus.Absent))
            {
                await statesman.ClearAsync(reference, "fixture", write, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!entry.HasValue)
            {
                continue;
            }

            Type type = ResolveType(entry, definition);
            object? value = JsonSerializer.Deserialize(entry.ValueJson!, type, serializer);
            if (value is null)
            {
                throw new StateFixtureException($"Fixture value for '{entry.Path}' deserialized to null.");
            }

            await statesman.SetAsync(reference, value, write, cancellationToken).ConfigureAwait(false);
            if (entry.Status is StateStatus.Invalidated or StateStatus.Faulted)
            {
                await statesman.InvalidateAsync(reference, $"fixture:{entry.Status}", write, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public static string ToJson(this StateFixture fixture, bool indented = true, JsonSerializerOptions? json = null)
    {
        JsonSerializerOptions options = json is null ? CreateJsonOptions() : new JsonSerializerOptions(json);
        options.WriteIndented = indented;
        return JsonSerializer.Serialize(fixture, options);
    }

    public static StateFixture FromJson(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<StateFixture>(json, options ?? DefaultJson)
        ?? throw new StateFixtureException("The fixture JSON did not contain a Statesman fixture.");

    public static async ValueTask SaveAsync(
        this StateFixture fixture,
        string path,
        bool indented = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, fixture.ToJson(indented), cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<StateFixture> LoadFixtureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return FromJson(json);
    }

    private static Type ResolveType(StateFixtureEntry entry, StateDefinitionManifest definition)
    {
        if (!string.IsNullOrWhiteSpace(entry.AssemblyQualifiedValueType))
        {
            Type? exact = Type.GetType(entry.AssemblyQualifiedValueType, throwOnError: false);
            if (exact is not null)
            {
                return exact;
            }
        }

        string expected = definition.ValueType;
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(entry.ValueType, throwOnError: false, ignoreCase: false);
            if (type is not null)
            {
                return type;
            }
        }

        throw new StateFixtureException(
            $"Could not resolve CLR type '{entry.ValueType}' for '{entry.Path}' (manifest type '{expected}').");
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> entry,
        IReadOnlyDictionary<string, string> fixture)
    {
        var merged = new Dictionary<string, string>(fixture, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in entry)
        {
            merged[key] = value;
        }

        merged["statesman.fixture"] = "true";
        return merged;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class StateFixtureException : InvalidOperationException
{
    public StateFixtureException(string message)
        : base(message)
    {
    }
}
