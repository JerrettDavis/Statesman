namespace Statesman.FileSystem.Tests;

public sealed class FileSystemPartitionCatalogTests
{
    [Fact]
    public async Task ListPartitionsAsync_yields_one_descriptor_per_distinct_address_with_its_latest_position()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "catalog", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var addressA = new StateAddress("app", "catalog/a", StatePartition.Default);
            var addressB = new StateAddress("app", "catalog/b", StatePartition.Default);
            await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
            StateAppendResult second = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
            StateAppendResult third = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            Assert.Equal(2, partitions.Count);
            StatePartitionDescriptor descriptorA = Assert.Single(partitions, value => value.Address.Equals(addressA));
            Assert.Equal(second.Record!.GlobalPosition, descriptorA.LastPosition.Position);
            StatePartitionDescriptor descriptorB = Assert.Single(partitions, value => value.Address.Equals(addressB));
            Assert.Equal(third.Record!.GlobalPosition, descriptorB.LastPosition.Position);
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
    public async Task ListPartitionsAsync_returns_nothing_when_the_store_has_never_been_written_to()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "catalog", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            Assert.Empty(partitions);
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
    public async Task ListPartitionsAsync_reflects_a_partition_written_through_ImportAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "catalog", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "catalog/item", StatePartition.Default);
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

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            StatePartitionDescriptor onlyDescriptor = Assert.Single(partitions);
            Assert.Equal(address, onlyDescriptor.Address);
            Assert.Equal(record.GlobalPosition, onlyDescriptor.LastPosition.Position);
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
