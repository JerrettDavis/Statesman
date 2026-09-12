using System.Globalization;

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

    [Fact]
    public async Task ListPartitionsAsync_does_not_list_a_torn_but_parseable_final_line()
    {
        // Phase 9 documented this asymmetry rather than fixing it: ReadAsync filters the phantom out
        // because dereferencing the record's history file fails, and ListPartitionsAsync had no
        // filter at all. A torn final line that still carries five tab-separated fields with
        // parseable numbers parses cleanly and used to yield a descriptor for an address the store
        // never wrote.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            var real = new StateAddress("app", "catalog/real", StatePartition.Default);
            await using (var store = new FileSystemStateLedgerStore("catalog", options))
            {
                await store.AppendAsync(real, StateWriteCondition.Absent, Commit("one"));
            }

            // Append a second, well-formed line for an address that was never written. Built from the
            // real line's own shape so the five fields and their separators are exactly right; only
            // the address differs, and no head or history file exists for it.
            string log = Path.Combine(directory, "_changes.log");
            string[] lines = (await File.ReadAllTextAsync(log)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] fields = lines[^1].Split('\t');
            Assert.Equal(5, fields.Length);
            fields[0] = (long.Parse(fields[0], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            fields[2] = "catalog/phantom";
            await File.AppendAllTextAsync(log, string.Join('\t', fields) + "\n");

            await using var reopened = new FileSystemStateLedgerStore("catalog", options);
            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in reopened.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            StatePartitionDescriptor only = Assert.Single(partitions);
            Assert.Equal(real, only.Address);
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
    public async Task Pruning_every_older_revision_keeps_the_partition_listed()
    {
        // A PINNING test, not a regression test: PruneAsync retains the newest revision on every
        // policy path (the MaxAge and KeepTombstones filters both keep Revision == latest, MaxRevisions
        // keeps the last N, and MaxBytes always seats the first record), so no shipped retention
        // sequence can ever empty a history directory while a head file remains. There is therefore no
        // lever today that distinguishes the head-file filter from a history-file filter -- this test
        // does not prove one mechanism wrong, it pins that the catalog and retention agree: a partition
        // that was genuinely written and then pruned down to one revision is still listed.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            var address = new StateAddress("app", "catalog/pruned", StatePartition.Default);
            await using var store = new FileSystemStateLedgerStore("catalog", options);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("one"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("two"));

            // KeepAll is the default and would prune nothing, so this test states a real retention
            // policy: MaxRevisions = 1 collapses the history to the newest revision.
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            StatePartitionDescriptor only = Assert.Single(partitions);
            Assert.Equal(address, only.Address);
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
