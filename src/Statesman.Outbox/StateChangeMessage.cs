using System.Globalization;

namespace Statesman.Outbox;

/// <summary>
/// One ledger change, flattened for delivery to a broker. Built by
/// <see cref="FromRecord(StateRecord, string, string?, string)"/> from a
/// <see cref="StateChangeEnvelope.Record"/> the change feed yielded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageId"/> is the semantic deduplication key and is unique by construction on every
/// shipped provider: an append allocates <c>revision = (current?.Revision ?? 0) + 1</c> under
/// per-address exclusion, so one store never produces two records with the same address and
/// revision. It survives an export/restore, because restore imports records exactly.
/// <see cref="GlobalPosition"/> is the <em>transport</em> deduplication key: unique within one
/// store's position lineage and meaningless across stores, which is why <see cref="Store"/> is a
/// first-class field.
/// </para>
/// <para>
/// <see cref="Payload"/> is carried opaquely and is never deserialized here — a store's
/// <see cref="IStateSerializer"/> is pluggable, so the bytes are not necessarily JSON.
/// <see cref="ContentType"/> is supplied from <see cref="OutboxOptions.PayloadContentType"/> and is
/// null exactly when there is no payload. <c>FreshUntil</c> and <c>ServeUntil</c> are deliberately
/// not carried: they are cache-policy fields meaningful only to the runtime that wrote them.
/// </para>
/// </remarks>
public sealed record StateChangeMessage
{
    /// <summary>The wire format identifier — always <see cref="StateChangeMessageFormat.Version"/>.</summary>
    public string Format { get; init; } = StateChangeMessageFormat.Version;

    /// <summary>The semantic deduplication key: <c>{Store}/{Address}#{Revision}</c>.</summary>
    public required string MessageId { get; init; }

    /// <summary>The <see cref="IStateLedgerStore.Name"/> the record was read from.</summary>
    public required string Store { get; init; }

    /// <summary>The declaring root (<see cref="StateAddress.Root"/>), case preserved.</summary>
    public required string Root { get; init; }

    /// <summary>The state path (<see cref="StatePath.Value"/>).</summary>
    public required string Path { get; init; }

    /// <summary>The partition (<see cref="StatePartition.Value"/>).</summary>
    public required string Partition { get; init; }

    /// <summary>The canonical address (<see cref="StateAddress.Canonical"/>), root lower-cased.</summary>
    public required string Address { get; init; }

    /// <summary>The record's revision within its address.</summary>
    public required long Revision { get; init; }

    /// <summary>The record's position in the store's global order. Exact as a JSON integer; consumers whose JSON parser uses doubles lose precision above 2^53 and should key on <see cref="MessageId"/>.</summary>
    public required long GlobalPosition { get; init; }

    /// <summary>When the writer allocated the record. An allocation timestamp on the writer's clock, not a commit timestamp.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The operation that produced the record.</summary>
    public required StateOperation Operation { get; init; }

    /// <summary>The resulting status.</summary>
    public required StateStatus Status { get; init; }

    /// <summary>The declared value type name.</summary>
    public required string ValueType { get; init; }

    /// <summary>The declared schema version of <see cref="Payload"/>.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>What wrote the record (<see cref="StateRecord.Source"/>).</summary>
    public required string Source { get; init; }

    /// <summary>The declaration fingerprint the payload was serialized under, when the outbox was given one. No store persists it, so it comes from <see cref="OutboxOptions.Fingerprint"/>.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The media type of <see cref="Payload"/>. Null exactly when there is no payload.</summary>
    public string? ContentType { get; init; }

    /// <summary>The serialized value, opaque. Null for records that carry none (cleared, invalidated, faulted).</summary>
    public byte[]? Payload { get; init; }

    /// <summary>The correlation id carried on the record, if any.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The causation id carried on the record, if any.</summary>
    public string? CausationId { get; init; }

    /// <summary>The record's metadata, copied.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The recorded fault, for a faulted record.</summary>
    public StateError? Error { get; init; }

    /// <summary>Flattens one ledger record into a wire message.</summary>
    /// <param name="record">The record the change feed yielded.</param>
    /// <param name="store">The <see cref="IStateLedgerStore.Name"/> it came from — part of <see cref="MessageId"/>.</param>
    /// <param name="fingerprint">The declaration fingerprint, or null when the outbox has no manifest.</param>
    /// <param name="contentType">The media type to stamp on a non-null payload.</param>
    public static StateChangeMessage FromRecord(
        StateRecord record,
        string store,
        string? fingerprint = null,
        string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        string canonical = record.Address.Canonical;
        return new StateChangeMessage
        {
            MessageId = string.Create(
                CultureInfo.InvariantCulture,
                $"{store}/{canonical}#{record.Revision}"),
            Store = store,
            Root = record.Address.Root,
            Path = record.Address.Path.Value,
            Partition = record.Address.Partition.Value,
            Address = canonical,
            Revision = record.Revision,
            GlobalPosition = record.GlobalPosition,
            OccurredAt = record.OccurredAt,
            Operation = record.Operation,
            Status = record.Status,
            ValueType = record.ValueType,
            SchemaVersion = record.SchemaVersion,
            Source = record.Source,
            Fingerprint = fingerprint,
            ContentType = record.Payload is null ? null : contentType,
            Payload = record.Payload,
            CorrelationId = record.CorrelationId,
            CausationId = record.CausationId,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Error = record.Error,
        };
    }
}
