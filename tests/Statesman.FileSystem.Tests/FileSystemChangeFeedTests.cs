namespace Statesman.FileSystem.Tests;

public sealed class FileSystemChangeFeedTests
{
    [Fact]
    public async Task ReadAsync_yields_records_after_the_given_cursor_across_streams_in_position_order()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
            var addressB = new StateAddress("app", "feed/b", StatePartition.Default);
            StateAppendResult first = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
            StateAppendResult second = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
            StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition)))
            {
                changes.Add(envelope);
            }

            Assert.Equal(2, changes.Count);
            Assert.Equal(second.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
            Assert.Equal(third.Record!.GlobalPosition, changes[1].Record.GlobalPosition);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadAsync_returns_nothing_when_the_store_has_never_been_written_to()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
            {
                changes.Add(envelope);
            }

            Assert.Empty(changes);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadAsync_reflects_records_written_through_ImportAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/item", StatePartition.Default);
            var record = new StateRecord
            {
                Address = address,
                Revision = 1,
                GlobalPosition = 1,
                OccurredAt = DateTimeOffset.UtcNow,
                Operation = StateOperation.Imported,
                Status = StateStatus.Ready,
                ValueType = typeof(string).FullName!,
                SchemaVersion = 1,
                Payload = "value"u8.ToArray(),
                Source = "test",
            };

            await store.ImportAsync(record);

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
            {
                changes.Add(envelope);
            }

            Assert.Single(changes);
            Assert.Equal(1, changes[0].Record.GlobalPosition);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
