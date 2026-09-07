using Statesman.TestHelpers;

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

    [Fact]
    public async Task ReadAsync_yields_a_repeated_ImportAsync_call_only_once()
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
            await store.ImportAsync(record);
            await store.ImportAsync(record);

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
            {
                changes.Add(envelope);
            }

            Assert.Single(changes);
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
    public async Task ReadAsync_does_not_throw_when_run_concurrently_with_many_AppendAsync_calls()
    {
        // Regression test for a Windows file-sharing violation: AppendChangeFeedEntryUnsafeAsync
        // acquires _changeFeedGate before appending to the change-feed file, but ReadAsync
        // used to read that same file without acquiring the gate. On Windows, a reader's
        // open (default FileShare.Read) does not grant the Write access a concurrent
        // writer's open needs, so the writer's File.AppendAllTextAsync open could throw
        // IOException while a read was in flight. Running many appends and reads
        // concurrently gives a reasonable chance of exposing that race if it still existed.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            const int writerCount = 8;
            const int readerCount = 8;
            const int writesPerWriter = 10;

            var writers = Enumerable.Range(0, writerCount).Select(async writerIndex =>
            {
                var address = new StateAddress("app", $"feed/concurrent-{writerIndex}", StatePartition.Default);
                for (int i = 0; i < writesPerWriter; i++)
                {
                    StateWriteCondition condition = i == 0
                        ? StateWriteCondition.Absent
                        : StateWriteCondition.AtRevision(i);
                    await store.AppendAsync(address, condition, Commit($"v{i}"));
                }
            });

            var readers = Enumerable.Range(0, readerCount).Select(async _ =>
            {
                for (int i = 0; i < writesPerWriter; i++)
                {
                    await foreach (StateChangeEnvelope _2 in store.ReadAsync(from: null))
                    {
                        // Draining the feed is the point of the test: it exercises
                        // File.ReadAllLinesAsync concurrently with File.AppendAllTextAsync.
                    }
                }
            });

            await Task.WhenAll(writers.Concat(readers));

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null))
            {
                changes.Add(envelope);
            }

            Assert.Equal(writerCount * writesPerWriter, changes.Count);
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
    public async Task AppendAsync_succeeds_normally_with_an_ordinary_cancellation_token()
    {
        // Regression guard for the CancellationToken.None change in AppendAsync: the
        // change-feed append after the primary write now uses CancellationToken.None
        // instead of the caller's token, since the primary write has already committed
        // by that point. This confirms the happy path (an ordinary, non-cancelled token)
        // still completes successfully and the change feed still reflects the append.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/ordinary", StatePartition.Default);
            using var cts = new CancellationTokenSource();

            StateAppendResult result = await store.AppendAsync(
                address, StateWriteCondition.Absent, Commit("v1"), cts.Token);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Record);

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, cts.Token))
            {
                changes.Add(envelope);
            }

            Assert.Single(changes);
            Assert.Equal(result.Record!.GlobalPosition, changes[0].Record.GlobalPosition);
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
    public async Task ReadAsync_never_skips_a_record_whose_append_was_in_flight_during_a_drain()
    {
        // The Phase 8 guarantee, stated operationally: a consumer that drains the feed while
        // another write is in flight, persists the cursor it got, and resumes from that cursor,
        // still receives the in-flight record. Before commit-time allocation, writer A allocated a
        // tick-based position and then spent two fsyncs before appending its change-log line,
        // during which writer B allocated a higher position, published, and let the consumer
        // persist a cursor above A's -- permanently and silently losing A.
        //
        // FlushToDisk is deliberately left at its default of true: that fsync is exactly what the
        // widened critical section now serializes, and disabling it here would test a
        // configuration nobody runs.
        //
        // After the fix the paused writer holds _changeFeedGate, which ReadAsync also acquires, so
        // BOTH writer B and the drain block until the clock is released. Neither is awaited before
        // the release, and each gets a bounded observation window instead. Today both windows
        // complete immediately; after the fix both expire, which is correct.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new PausingTimeProvider(pauseOnCall: 2);
            await using var store = new FileSystemStateLedgerStore(
                "feed",
                new FileSystemStateLedgerStoreOptions { RootDirectory = directory },
                clock);
            var addressA = new StateAddress("app", "feed/a", StatePartition.Default);
            var addressB = new StateAddress("app", "feed/b", StatePartition.Default);

            Task<StateAppendResult> writerA = Task.Run(() =>
                store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a")).AsTask());
            Task<StateAppendResult> writerB;
            Task<List<StateChangeEnvelope>> drain;
            try
            {
                await clock.WaitForPauseAsync(TimeSpan.FromSeconds(10));

                writerB = Task.Run(() =>
                    store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b")).AsTask());
                await Task.WhenAny(writerB, Task.Delay(TimeSpan.FromSeconds(2)));

                drain = Task.Run(() => DrainAsync(store, from: null));
                await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            finally
            {
                clock.Release();
            }

            await writerA;
            await writerB;
            List<StateChangeEnvelope> firstBatch = await drain;

            StateChangeCursor? cursor = firstBatch.Count == 0 ? null : firstBatch[^1].Cursor;
            List<StateChangeEnvelope> secondBatch = await DrainAsync(store, cursor);

            HashSet<(string Address, long Revision)> seen =
            [
                .. firstBatch.Concat(secondBatch)
                    .Select(envelope => (envelope.Record.Address.Canonical, envelope.Record.Revision)),
            ];
            Assert.Contains((addressA.Canonical, 1L), seen);
            Assert.Contains((addressB.Canonical, 1L), seen);
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
    public void FlushToDisk_defaults_to_true()
    {
        // Pinned because the change-feed tests above are only meaningful at this default: the
        // widened critical section serializes one WriteThrough fsync per append, and a future
        // flip of this default would quietly change what those tests measure. This is the Phase 7
        // lesson -- do not disable the durability the fix is serializing behind in order to make a
        // test fast, and make the default itself a tested value.
        var options = new FileSystemStateLedgerStoreOptions { RootDirectory = "unused" };

        Assert.True(options.FlushToDisk);
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from))
        {
            changes.Add(envelope);
        }

        return changes;
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
