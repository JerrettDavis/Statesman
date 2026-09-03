using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Statesman.Testing;

namespace Statesman.FileSystem.Tests;

public sealed class FileSystemLedgerTests
{
    private static readonly StateKey<StoredValue> Key = StateKey.Define<StoredValue>("stored/value");

    [Fact]
    public async Task State_survives_a_new_runtime_and_store_instance()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("filesystem")
                .State(Key, state => state.StoreWith("file"))
                .Build();

            await using (StatesmanTestHarness first = StatesmanTestHarness.Create(
                declaration,
                stores: new[]
                {
                    new FileSystemStateLedgerStore("file", new FileSystemStateLedgerStoreOptions { RootDirectory = directory }),
                }))
            {
                await first.Runtime.State(Key).SetAsync(new StoredValue("persisted"));
            }

            await using StatesmanTestHarness second = StatesmanTestHarness.Create(
                declaration,
                stores: new[]
                {
                    new FileSystemStateLedgerStore("file", new FileSystemStateLedgerStoreOptions { RootDirectory = directory }),
                });
            IStateSnapshot<StoredValue> restored = await second.Runtime.State(Key).GetAsync();

            Assert.Equal("persisted", restored.RequiredValue.Value);
            Assert.Equal(1, restored.Revision);
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
    public async Task Exact_import_repairs_a_divergent_cached_revision()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "file",
                new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            StateAddress address = new("filesystem-import", Key.Path, StatePartition.Default);
            StateRecord original = Record(address, "stale");
            StateRecord authoritative = original with
            {
                Payload = Encoding.UTF8.GetBytes("authoritative"),
                Source = "cold-authority",
            };

            await store.ImportAsync(original);
            await store.ImportAsync(authoritative);

            StateRecord? latest = await store.ReadLatestAsync(address);
            var history = new List<StateRecord>();
            await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions()))
            {
                history.Add(record);
            }

            StateRecord persisted = latest ?? throw new InvalidOperationException("The imported record was not persisted.");
            Assert.Equal(authoritative.Payload, persisted.Payload);
            Assert.Equal("cold-authority", persisted.Source);
            Assert.Single(history);
            Assert.Equal(authoritative.Payload, history[0].Payload);
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
    public async Task Service_collection_extension_registers_the_file_system_store()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("filesystem-di")
                .State(Key, state => state.StoreWith("file"))
                .Build();
            var services = new ServiceCollection();
            services.AddStatesman(
                declaration,
                builder => builder.UseFileSystemStore("file", directory));

            await using ServiceProvider provider = services.BuildServiceProvider();
            IStatesman runtime = provider.GetRequiredService<IStatesmanRegistry>().Get("filesystem-di");

            await runtime.State(Key).SetAsync(new StoredValue("configured"));
            IStateSnapshot<StoredValue> snapshot = await runtime.State(Key).GetAsync(StateReadOptions.Cached);

            Assert.Equal("configured", snapshot.RequiredValue.Value);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StateRecord Record(StateAddress address, string value) => new()
    {
        Address = address,
        Revision = 1,
        GlobalPosition = 1,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed record StoredValue(string Value);
}
