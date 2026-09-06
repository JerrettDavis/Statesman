namespace Statesman.Outbox.Tests;

/// <summary>Builds ledger records at exact positions for injection through <see cref="IStateLedgerReplica"/>.</summary>
internal static class OutboxTestRecords
{
    public const string Root = "app";

    public static StateRecord Record(long position, long revision = 1, string path = "orders/basket") =>
        new()
        {
            Address = new StateAddress(Root, path, StatePartition.Default),
            Revision = revision,
            GlobalPosition = position,
            OccurredAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero).AddSeconds(position),
            Operation = StateOperation.Set,
            Status = StateStatus.Ready,
            ValueType = "Contoso.Basket",
            SchemaVersion = 1,
            Payload = System.Text.Encoding.UTF8.GetBytes($"{{\"position\":{position}}}"),
            Source = "test",
        };

    public static async Task SeedAsync(InMemoryStateLedgerStore store, params long[] positions)
    {
        long revision = 0;
        foreach (long position in positions)
        {
            revision++;
            await store.ImportAsync(Record(position, revision));
        }
    }
}
