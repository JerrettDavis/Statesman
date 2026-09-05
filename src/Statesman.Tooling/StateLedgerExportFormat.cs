using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman.Tooling;

/// <summary>
/// The on-disk shape of a ledger export: newline-delimited JSON with exactly one
/// <see cref="StateLedgerExportHeader"/> line first, one <see cref="StateLedgerExportRecord"/> line per
/// record, and exactly one <see cref="StateLedgerExportTrailer"/> line last. Restore validates the header
/// before parsing any record and the trailer's counts before importing any record, so a foreign or
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

    /// <summary>The serializer options every line is written and read with: camelCase, enums as strings, nulls omitted.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
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
    public string Root { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Partition { get; init; } = StatePartition.Default.Value;

    public long Revision { get; init; }

    public long GlobalPosition { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public StateOperation Operation { get; init; }

    public StateStatus Status { get; init; }

    public string ValueType { get; init; } = string.Empty;

    public int SchemaVersion { get; init; }

    public byte[]? Payload { get; init; }

    public DateTimeOffset? FreshUntil { get; init; }

    public DateTimeOffset? ServeUntil { get; init; }

    public string Source { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
