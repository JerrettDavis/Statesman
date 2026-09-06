using System.Text;
using System.Text.Json;

namespace Statesman.Outbox.Tests;

public sealed class StateChangeMessageTests
{
    private static StateRecord Record(
        StateOperation operation = StateOperation.Set,
        StateStatus status = StateStatus.Ready,
        byte[]? payload = null) =>
        new()
        {
            Address = new StateAddress("App", "orders/basket", new StatePartition("tenant-7")),
            Revision = 4,
            GlobalPosition = 638000000000000000,
            OccurredAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
            Operation = operation,
            Status = status,
            ValueType = "Contoso.Basket",
            SchemaVersion = 2,
            Payload = payload,
            Source = "runtime",
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["region"] = "eu" },
        };

    [Fact]
    public void FromRecord_carries_every_field_it_declares()
    {
        StateChangeMessage message = StateChangeMessage.FromRecord(
            Record(payload: Encoding.UTF8.GetBytes("{}")),
            store: "primary",
            fingerprint: "fp-abc");

        Assert.Equal("statesman.state-change/v1", message.Format);
        Assert.Equal("primary", message.Store);
        Assert.Equal("App", message.Root);
        Assert.Equal("orders/basket", message.Path);
        Assert.Equal("tenant-7", message.Partition);
        Assert.Equal("app::orders/basket::tenant-7", message.Address);
        Assert.Equal(4, message.Revision);
        Assert.Equal(638000000000000000, message.GlobalPosition);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), message.OccurredAt);
        Assert.Equal(StateOperation.Set, message.Operation);
        Assert.Equal(StateStatus.Ready, message.Status);
        Assert.Equal("Contoso.Basket", message.ValueType);
        Assert.Equal(2, message.SchemaVersion);
        Assert.Equal("runtime", message.Source);
        Assert.Equal("fp-abc", message.Fingerprint);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal("{}", Encoding.UTF8.GetString(message.Payload!));
        Assert.Equal("corr-1", message.CorrelationId);
        Assert.Equal("cause-1", message.CausationId);
        Assert.Equal("eu", message.Metadata["region"]);
        Assert.Null(message.Error);
    }

    [Fact]
    public void MessageId_is_store_slash_canonical_hash_revision()
    {
        StateChangeMessage message = StateChangeMessage.FromRecord(Record(), store: "primary");

        Assert.Equal("primary/app::orders/basket::tenant-7#4", message.MessageId);
    }

    [Fact]
    public void A_cleared_record_carries_no_payload_and_no_content_type()
    {
        StateChangeMessage message = StateChangeMessage.FromRecord(
            Record(StateOperation.Cleared, StateStatus.Cleared),
            store: "primary");

        Assert.Null(message.Payload);
        Assert.Null(message.ContentType);
    }

    [Fact]
    public void A_null_fingerprint_is_omitted_from_the_serialized_message()
    {
        StateChangeMessage withFingerprint = StateChangeMessage.FromRecord(Record(), "primary", "fp-abc");
        StateChangeMessage withoutFingerprint = StateChangeMessage.FromRecord(Record(), "primary");

        string with = JsonSerializer.Serialize(withFingerprint, StateChangeMessageFormat.Json);
        string without = JsonSerializer.Serialize(withoutFingerprint, StateChangeMessageFormat.Json);

        Assert.Contains("\"fingerprint\":\"fp-abc\"", with, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", without, StringComparison.Ordinal);
    }

    [Fact]
    public void Enums_serialize_as_names_and_the_message_round_trips()
    {
        StateChangeMessage message = StateChangeMessage.FromRecord(
            Record(payload: Encoding.UTF8.GetBytes("{}")),
            store: "primary",
            fingerprint: "fp-abc");

        string json = JsonSerializer.Serialize(message, StateChangeMessageFormat.Json);
        Assert.Contains("\"operation\":\"Set\"", json, StringComparison.Ordinal);

        StateChangeMessage? restored = JsonSerializer.Deserialize<StateChangeMessage>(json, StateChangeMessageFormat.Json);

        Assert.NotNull(restored);
        Assert.Equal(message.MessageId, restored!.MessageId);
        Assert.Equal(message.GlobalPosition, restored.GlobalPosition);
        Assert.Equal(message.Payload, restored.Payload);
    }
}
