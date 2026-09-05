using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman.Tooling;

/// <summary>
/// The on-disk shape of a ledger export: newline-delimited JSON with exactly one
/// <see cref="StateLedgerExportHeader"/> line first, one <see cref="StateLedgerExportRecord"/> line per
/// record, and exactly one <see cref="StateLedgerExportTrailer"/> line last. Restore validates the header
/// before parsing any record and the trailer's record count before importing any record, so a foreign or
/// truncated file never partially restores.
/// </summary>
public static class StateLedgerExportFormat
{
    /// <summary>
    /// The format identifier written into every header. Restore refuses any other value. Bump this
    /// when the line shapes change incompatibly; per-record <see cref="StateRecord.SchemaVersion"/> is
    /// unrelated and is carried verbatim.
    /// </summary>
    public const string Version = "statesman.ledger-export/v1";

    /// <summary>The serializer options every line is written and read with: camelCase, enums as strings, nulls omitted. Read-only.</summary>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>The first line of an export: what was exported, from which declaration, and when.</summary>
public sealed record StateLedgerExportHeader
{
    /// <summary>Must equal <see cref="StateLedgerExportFormat.Version"/>.</summary>
    public required string Format { get; init; }

    /// <summary>The Statesman root (<see cref="StatesmanManifest.Id"/>) whose partitions were exported.</summary>
    public required string Root { get; init; }

    /// <summary>The <see cref="StatesmanManifest.Fingerprint"/> of the declaration the source store was serving.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The <see cref="IStateLedgerStore.Name"/> of the source store. Informational only.</summary>
    public required string Store { get; init; }

    /// <summary>When the export began.</summary>
    public required DateTimeOffset ExportedAt { get; init; }

    /// <summary>Free-form operator metadata (environment, ticket, operator) copied from <see cref="StateLedgerExportOptions.Metadata"/>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The last line of an export. A file without one, or whose counts disagree with its contents, is truncated or corrupt.</summary>
public sealed record StateLedgerExportTrailer
{
    /// <summary>The number of record lines between the header and this trailer.</summary>
    public required long Records { get; init; }

    /// <summary>The number of distinct partitions those records belong to.</summary>
    public required int Partitions { get; init; }
}

/// <summary>One exported <see cref="StateRecord"/>, flattened so the address is three plain strings.</summary>
public sealed record StateLedgerExportRecord
{
    /// <summary>The address root (<see cref="StateAddress.Root"/>).</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>The state path (<see cref="StatePath.Value"/>).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The partition value (<see cref="StatePartition.Value"/>); "default" for the default partition.</summary>
    public string Partition { get; init; } = StatePartition.Default.Value;

    /// <summary>Mirrors <see cref="StateRecord.Revision"/>.</summary>
    public long Revision { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.GlobalPosition"/>.</summary>
    public long GlobalPosition { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.OccurredAt"/>.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.Operation"/>.</summary>
    public StateOperation Operation { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.Status"/>.</summary>
    public StateStatus Status { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.ValueType"/>.</summary>
    public string ValueType { get; init; } = string.Empty;

    /// <summary>Mirrors <see cref="StateRecord.SchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.Payload"/>.</summary>
    public byte[]? Payload { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.FreshUntil"/>.</summary>
    public DateTimeOffset? FreshUntil { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.ServeUntil"/>.</summary>
    public DateTimeOffset? ServeUntil { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.Source"/>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Mirrors <see cref="StateRecord.CorrelationId"/>.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.CausationId"/>.</summary>
    public string? CausationId { get; init; }

    /// <summary>Mirrors <see cref="StateRecord.Metadata"/>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mirrors <see cref="StateRecord.Error"/>.</summary>
    public StateError? Error { get; init; }

    /// <summary>Flattens a ledger record for serialization.</summary>
    public static StateLedgerExportRecord FromRecord(StateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new StateLedgerExportRecord
        {
            Root = record.Address.Root,
            Path = record.Address.Path.Value,
            Partition = record.Address.Partition.Value,
            Revision = record.Revision,
            GlobalPosition = record.GlobalPosition,
            OccurredAt = record.OccurredAt,
            Operation = record.Operation,
            Status = record.Status,
            ValueType = record.ValueType,
            SchemaVersion = record.SchemaVersion,
            Payload = record.Payload?.ToArray(),
            FreshUntil = record.FreshUntil,
            ServeUntil = record.ServeUntil,
            Source = record.Source,
            CorrelationId = record.CorrelationId,
            CausationId = record.CausationId,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Error = record.Error,
        };
    }

    /// <summary>Rebuilds the ledger record. Does not validate; callers run <see cref="StateRecord.Validate"/>.</summary>
    public StateRecord ToRecord() => new()
    {
        Address = new StateAddress(Root, new StatePath(Path), new StatePartition(Partition)),
        Revision = Revision,
        GlobalPosition = GlobalPosition,
        OccurredAt = OccurredAt,
        Operation = Operation,
        Status = Status,
        ValueType = ValueType,
        SchemaVersion = SchemaVersion,
        Payload = Payload?.ToArray(),
        FreshUntil = FreshUntil,
        ServeUntil = ServeUntil,
        Source = Source,
        CorrelationId = CorrelationId,
        CausationId = CausationId,
        Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase),
        Error = Error,
    };
}
