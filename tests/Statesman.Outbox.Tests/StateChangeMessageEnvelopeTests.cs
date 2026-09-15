using System.Text;
using System.Text.Json;

namespace Statesman.Outbox.Tests;

/// <summary>
/// What an outbox message takes from the record's own <see cref="StateRecord.Envelope"/> and what it
/// still takes from <see cref="OutboxOptions"/>.
/// </summary>
/// <remarks>
/// Before ROADMAP 0.3 Phase 22 no store persisted a content type, a serializer id or a fingerprint,
/// so all three were declared by configuration and a message could disagree with the record it was
/// built from. Every fact here uses options that deliberately differ from the envelope, so a row that
/// reads the wrong source fails rather than coincidentally agreeing.
/// </remarks>
public sealed class StateChangeMessageEnvelopeTests
{
    private const string OptionContentType = "application/x-option";
    private const string OptionFingerprint = "fp-option";

    private static readonly StateEnvelope Envelope = new()
    {
        ContentType = "application/x-envelope",
        SerializerId = "probe.serializer/v3",
        Fingerprint = "fp-envelope",
    };

    private static StateRecord Record(StateEnvelope? envelope, bool withPayload = true) => new()
    {
        Address = new StateAddress("App", "orders/basket", new StatePartition("tenant-7")),
        Revision = 4,
        GlobalPosition = 638000000000000000,
        OccurredAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
        Operation = withPayload ? StateOperation.Set : StateOperation.Cleared,
        Status = withPayload ? StateStatus.Ready : StateStatus.Cleared,
        ValueType = "Contoso.Basket",
        SchemaVersion = 2,
        Payload = withPayload ? Encoding.UTF8.GetBytes("{}") : null,
        Envelope = envelope,
        Source = "runtime",
    };

    private static StateChangeMessage Build(StateRecord record) =>
        StateChangeMessage.FromRecord(record, "primary", OptionFingerprint, OptionContentType);

    [Fact]
    public void An_envelope_supplies_the_content_type_in_place_of_the_option()
    {
        Assert.Equal("application/x-envelope", Build(Record(Envelope)).ContentType);
    }

    [Fact]
    public void An_envelope_supplies_the_fingerprint_in_place_of_the_option()
    {
        Assert.Equal("fp-envelope", Build(Record(Envelope)).Fingerprint);
    }

    [Fact]
    public void An_envelope_supplies_the_serializer_id_which_has_no_configured_fallback()
    {
        // The one field with no option behind it: a serializer id the writer did not record is not
        // something the outbox can honestly assert, so it is null rather than guessed.
        Assert.Equal("probe.serializer/v3", Build(Record(Envelope)).SerializerId);
    }

    [Fact]
    public void A_record_without_an_envelope_still_takes_both_values_from_the_options()
    {
        // The legacy path, unchanged. Every record a consumer's store already holds reaches this
        // branch, and it must produce exactly the message previous releases produced.
        StateChangeMessage message = Build(Record(envelope: null));

        Assert.Equal(OptionContentType, message.ContentType);
        Assert.Equal(OptionFingerprint, message.Fingerprint);
        Assert.Null(message.SerializerId);
    }

    [Fact]
    public void An_envelope_without_a_fingerprint_falls_back_to_the_option()
    {
        // A populated envelope whose own Fingerprint is null means the writer had no declaration to
        // name, not that the outbox's configured fingerprint is wrong. The content type and the
        // serializer id still come from the envelope, so this row pins the inner fallback alone.
        StateChangeMessage message = Build(Record(new StateEnvelope
        {
            ContentType = "application/x-envelope",
            SerializerId = "probe.serializer/v3",
        }));

        Assert.Equal(OptionFingerprint, message.Fingerprint);
        Assert.Equal("application/x-envelope", message.ContentType);
        Assert.Equal("probe.serializer/v3", message.SerializerId);
    }

    [Fact]
    public void A_record_with_an_envelope_and_no_payload_still_carries_neither_content_type_nor_serializer_id()
    {
        // "Null exactly when there is no payload" is a wire contract this change must not break, and
        // the serializer id joins it for the same reason: both describe bytes that are not there.
        // The runtime never stamps an envelope on a payload-less commit, so this shape reaches the
        // outbox only from a hand-built or foreign record, which is exactly why it is pinned.
        StateChangeMessage message = Build(Record(Envelope, withPayload: false));

        Assert.Null(message.Payload);
        Assert.Null(message.ContentType);
        Assert.Null(message.SerializerId);

        // The fingerprint is NOT gated on the payload: it describes the declaration, not the bytes.
        Assert.Equal("fp-envelope", message.Fingerprint);
    }

    [Fact]
    public void The_serializer_id_is_omitted_from_the_serialized_message_when_absent()
    {
        // The additive-JSON claim, made checkable. A message built from a pre-envelope record
        // carries no serializerId key at all, so an existing broker consumer sees the same document
        // it has always seen and StateChangeMessageFormat.Version does not bump.
        string legacy = JsonSerializer.Serialize(Build(Record(envelope: null)), StateChangeMessageFormat.Json);
        string enveloped = JsonSerializer.Serialize(Build(Record(Envelope)), StateChangeMessageFormat.Json);

        Assert.DoesNotContain("serializerId", legacy, StringComparison.Ordinal);
        Assert.Contains("\"serializerId\":\"probe.serializer/v3\"", enveloped, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"statesman.state-change/v1\"", enveloped, StringComparison.Ordinal);
    }
}
