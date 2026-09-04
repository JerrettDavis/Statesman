namespace Statesman.Tests;

public sealed class StateChangeFeedTests
{
    [Fact]
    public void StateChangeCursor_rejects_non_positive_positions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateChangeCursor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateChangeCursor(-1));
    }

    [Fact]
    public void StateChangeCursor_accepts_and_exposes_a_positive_position()
    {
        var cursor = new StateChangeCursor(5);

        Assert.Equal(5, cursor.Position);
    }

    [Fact]
    public void StateChangeEnvelope_carries_a_record_and_a_cursor()
    {
        var record = new StateRecord
        {
            Address = new StateAddress("app", "feed/item", StatePartition.Default),
            Revision = 1,
            GlobalPosition = 7,
            OccurredAt = DateTimeOffset.UtcNow,
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = "value"u8.ToArray(),
            Source = "test",
        };
        var cursor = new StateChangeCursor(7);

        var envelope = new StateChangeEnvelope { Record = record, Cursor = cursor };

        Assert.Same(record, envelope.Record);
        Assert.Equal(cursor, envelope.Cursor);
    }
}
