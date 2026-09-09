using System.Text.Json;
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
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(new StateChangeCursor(first.Record!.GlobalPosition), StateChangeReadOptions.Default))
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
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
                    await foreach (StateChangeEnvelope _2 in store.ReadAsync(from: null, StateChangeReadOptions.Default))
                    {
                        // Draining the feed is the point of the test: it exercises
                        // File.ReadAllLinesAsync concurrently with File.AppendAllTextAsync.
                    }
                }
            });

            await Task.WhenAll(writers.Concat(readers));

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
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
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default, cts.Token))
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
    public async Task AppendAsync_succeeds_while_a_second_process_reader_holds_the_change_log_open()
    {
        // Regression test for the cross-process reader gap: ReadAsync used to open _changes.log
        // with File.ReadAllLinesAsync's default share mode (FileShare.Read only, Write excluded),
        // which on Windows does not grant the Write access a concurrent writer's open needs -- so
        // a reader in ANOTHER process holding the file open made AppendAsync throw IOException. A
        // second process cannot be spawned from inside this one process for a unit test, but the
        // failure mode is reproducible from inside one process: open a second, independent handle
        // to the same file with the share semantics a second-process reader now gets from
        // ReadAsync's fix (FileAccess.Read, FileShare.ReadWrite), which is exactly what that
        // reader's open looks like from the writer's perspective. Confirmed RED before this fix:
        // opening the same second handle with the OLD default share (FileShare.Read, Write
        // excluded -- what File.OpenRead / File.ReadAllLinesAsync used) makes the AppendAsync
        // below throw "The process cannot access the file '...\_changes.log' because it is being
        // used by another process." With the fix, FileShare.ReadWrite no longer blocks the writer.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/cross-process", StatePartition.Default);

            // Seed the change log so it exists before the second handle opens it.
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("seed"));
            string changeLogFile = Path.Combine(directory, "_changes.log");

            using (new FileStream(changeLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                StateAppendResult result = await store.AppendAsync(
                    address, StateWriteCondition.AtRevision(1), Commit("second"));

                Assert.True(result.Succeeded);
            }

            List<StateChangeEnvelope> changes = [];
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(from: null, StateChangeReadOptions.Default))
            {
                changes.Add(envelope);
            }

            Assert.Equal(2, changes.Count);
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

    [Fact]
    public async Task ReadAsync_skips_a_torn_final_line_and_still_yields_the_intact_prefix()
    {
        // A cross-process reader can observe the log mid-append: File.AppendAllTextAsync is not
        // atomic, so the FINAL line can be a prefix of a real one. Before the guard this threw
        // IndexOutOfRangeException out of the feed.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var store = new FileSystemStateLedgerStore("feed", options);
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"))).Succeeded);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"))).Succeeded);

            await File.AppendAllTextAsync(Path.Combine(directory, "_changes.log"), "638000000000000000\tapp\tfeed");

            List<StateChangeEnvelope> changes = await DrainAsync(store, from: null);

            Assert.Equal(2, changes.Count);
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
    public async Task ReadAsync_throws_a_clear_error_when_a_malformed_line_is_not_the_last()
    {
        // Only the final line can be a partially-written append. A malformed line with completed
        // lines after it is corruption, and silently skipping it would drop records from a feed
        // documented as lossless within retention.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var store = new FileSystemStateLedgerStore("feed", options);
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"))).Succeeded);

            await File.AppendAllTextAsync(Path.Combine(directory, "_changes.log"), "not-a-position\tapp\n");

            // A real append after the damage, so the malformed line is not the final one.
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"))).Succeeded);

            InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await DrainAsync(store, from: null));

            Assert.Contains("_changes.log", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("line 2", thrown.Message, StringComparison.Ordinal);
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
    public async Task ListPartitionsAsync_skips_a_torn_final_line_too()
    {
        // The partition catalog parses the same lines the same way and had the same defect.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var store = new FileSystemStateLedgerStore("feed", options);
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"))).Succeeded);

            await File.AppendAllTextAsync(Path.Combine(directory, "_changes.log"), "638000000000000000\tapp\tfeed");

            List<StatePartitionDescriptor> partitions = [];
            await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
            {
                partitions.Add(descriptor);
            }

            Assert.Single(partitions);
            Assert.Equal(address.Canonical, partitions[0].Address.Canonical);
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
    public async Task An_append_after_a_durably_torn_tail_starts_a_fresh_line_and_reports_the_tear()
    {
        // The crash-durable shape the in-flight guard above does not cover: the log's last byte is
        // not a newline, so a blind append would merge this record into the torn prefix. The merged
        // line would still be the FINAL line, so ReadAsync's torn-tail guard would skip it too and
        // the committed record would be missing from a feed documented as lossless -- silently,
        // with AppendAsync still reporting success. The shipped behaviour starts a fresh line
        // instead, which leaves the torn prefix as a malformed line that is no longer last: the
        // very next read throws InvalidDataException naming it. Loud and recoverable, never lost.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var store = new FileSystemStateLedgerStore("feed", options);
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"))).Succeeded);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"))).Succeeded);

            // No trailing newline: a partial line already on disk, not one in flight.
            string log = Path.Combine(directory, "_changes.log");
            await File.AppendAllTextAsync(log, "638000000000000000\tapp\tfeed");

            StateAppendResult third = await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("a3"));
            Assert.True(third.Succeeded);

            // The record is committed and readable by address: a tear costs a read of the feed,
            // never the record itself.
            StateRecord? latest = await store.ReadLatestAsync(address);
            Assert.NotNull(latest);
            Assert.Equal(3, latest!.Revision);

            // No silent loss: the feed reports corruption rather than coming back one record short.
            InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await DrainAsync(store, from: null));
            Assert.Contains("_changes.log", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("line 3", thrown.Message, StringComparison.Ordinal);

            // The new entry is a line of its own, not merged into the torn prefix.
            string[] lines = await File.ReadAllLinesAsync(log);
            Assert.Equal(4, lines.Length);
            Assert.Equal("638000000000000000\tapp\tfeed", lines[2]);
            Assert.StartsWith(
                third.Record!.GlobalPosition.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t",
                lines[3],
                StringComparison.Ordinal);

            // The documented recovery: truncate the partial line. The whole feed comes back,
            // including the record appended after the tear.
            await File.WriteAllTextAsync(log, string.Join('\n', lines[0], lines[1], lines[3]) + "\n");
            List<StateChangeEnvelope> repaired = await DrainAsync(store, from: null);
            Assert.Equal(3, repaired.Count);
            Assert.Equal(third.Record!.GlobalPosition, repaired[2].Record.GlobalPosition);
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
    public async Task An_append_to_a_well_formed_log_adds_no_blank_line()
    {
        // The other half of the fresh-line rule: a log that already ends in a newline must not gain
        // an empty line, which would be a byte of noise on every append and would shift the line
        // numbers the corruption message reports.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var store = new FileSystemStateLedgerStore("feed", options);
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"))).Succeeded);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"))).Succeeded);
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("a3"))).Succeeded);

            string text = await File.ReadAllTextAsync(Path.Combine(directory, "_changes.log"));
            Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
            Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            List<StateChangeEnvelope> changes = await DrainAsync(store, from: null);
            Assert.Equal(3, changes.Count);
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
    public async Task A_capped_read_does_not_open_the_history_files_above_the_page()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/capped", StatePartition.Default);
            for (int revision = 0; revision < 10; revision++)
            {
                StateWriteCondition condition = revision == 0
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision);
                await store.AppendAsync(address, condition, Commit($"v{revision}"));
            }

            // One address means one history directory whose files are named by revision, so ordinal
            // name order is position order. Revision 6's file becomes unparseable.
            string history = Directory
                .GetDirectories(directory, "history", SearchOption.AllDirectories)
                .Single();
            string sixth = Directory
                .GetFiles(history, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ElementAt(5);
            await File.WriteAllTextAsync(sixth, "this is not json");

            var read = new List<StateChangeEnvelope>();
            await foreach (StateChangeEnvelope envelope in store.ReadAsync(
                from: null, new StateChangeReadOptions { Take = 5 }))
            {
                read.Add(envelope);
            }

            Assert.Equal(5, read.Count);
            Assert.Equal([1L, 2L, 3L, 4L, 5L], read.Select(envelope => envelope.Record.Revision).ToArray());

            // The proof cuts both ways: an uncapped read DOES reach that file and fails, so the
            // capped read above was genuinely not opening it rather than merely tolerating it.
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (StateChangeEnvelope _ in store.ReadAsync(from: null, StateChangeReadOptions.Default))
                {
                }
            });
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
    public async Task A_capped_read_skips_past_dangling_entries_to_fill_its_page()
    {
        // Three pruned records followed by two live ones. Take = 2 must yield BOTH live records: the
        // dangling entries are skipped and do not count against the page. An implementation that
        // bounded PARSED entries instead would stop after the first two dangling lines and return an
        // empty page -- and a paging consumer reads an empty page as "caught up", so the outbox's
        // cursor would stall at that position forever while committed records sat above it.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var pruned = new StateAddress("app", "feed/pruned", StatePartition.Default);
            for (int revision = 0; revision < 3; revision++)
            {
                StateWriteCondition condition = revision == 0
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision);
                await store.AppendAsync(pruned, condition, Commit($"gone{revision}"));
            }

            var live = new StateAddress("app", "feed/live", StatePartition.Default);
            await store.AppendAsync(live, StateWriteCondition.Absent, Commit("kept1"));
            await store.AppendAsync(live, StateWriteCondition.AtRevision(1), Commit("kept2"));

            // Delete every history file under the pruned address, leaving all three of its
            // change-log lines behind. That is the dangling-entry shape, produced here by hand so the
            // test does not depend on which files PruneAsync happens to keep.
            string prunedHistory = Path.Combine(
                Directory
                    .GetDirectories(directory, "history", SearchOption.AllDirectories)
                    .Single(path => Directory.GetFiles(path, "*.json").Length == 3));
            foreach (string file in Directory.GetFiles(prunedHistory, "*.json"))
            {
                File.Delete(file);
            }

            List<StateChangeEnvelope> page = await DrainAsync(
                store, from: null, new StateChangeReadOptions { Take = 2 });

            Assert.Equal(2, page.Count);
            Assert.Equal(
                [live.Canonical, live.Canonical],
                page.Select(envelope => envelope.Record.Address.Canonical).ToArray());
            Assert.Equal([1L, 2L], page.Select(envelope => envelope.Record.Revision).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from,
        StateChangeReadOptions? options = null)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from, options ?? StateChangeReadOptions.Default))
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
