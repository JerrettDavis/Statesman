using System.Text;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// The change-log compactor: what it drops, what it must never touch, and what it does not break.
/// </summary>
/// <remarks>
/// Every test observes the compactor through <c>ReadAsync</c>'s output and through the bytes on
/// disk. There is no <c>[InternalsVisibleTo]</c> in this repository and this phase adds none, so the
/// index, the drop predicate and the temp-and-rename are all verified by their effects.
/// </remarks>
public sealed class FileSystemChangeLogCompactionTests
{
    [Fact]
    public async Task Compaction_drops_a_pruned_records_dangling_line_and_leaves_the_feed_unchanged()
    {
        // Proof (a). PruneAsync deletes the history file and leaves the change-log line behind;
        // ReadAsync skips it, so the feed is already right and only the FILE is wrong. A compactor
        // that dropped a live line would satisfy "the log shrank" and fail the equality below, which
        // is why both halves are asserted together.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var addressA = new StateAddress("app", "compact/a", StatePartition.Default);
            var addressB = new StateAddress("app", "compact/b", StatePartition.Default);
            await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
            await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));
            await store.AppendAsync(addressA, StateWriteCondition.AtRevision(2), Commit("a3"));
            await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

            await store.PruneAsync(addressA, new StateRetentionPolicy { MaxRevisions = 1 });

            (long[] cursorsBefore, string[] payloadsBefore) = await DrainAsync(store);
            long bytesBefore = new FileInfo(ChangeLog(directory)).Length;

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();

            Assert.False(result.DryRun);
            Assert.Equal(4, result.LinesBefore);
            Assert.Equal(2, result.LinesAfter);
            Assert.Equal(bytesBefore, result.BytesBefore);
            Assert.Equal(new FileInfo(ChangeLog(directory)).Length, result.BytesAfter);
            Assert.True(result.BytesReclaimed > 0);

            (long[] cursorsAfter, string[] payloadsAfter) = await DrainAsync(store);
            Assert.Equal(cursorsBefore, cursorsAfter);
            Assert.Equal(payloadsBefore, payloadsAfter);

            // And a store instance that has never seen the old log agrees, which is what proves the
            // compacted file stands on its own rather than being propped up by a warm index.
            await using var fresh = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            (long[] cursorsFresh, string[] payloadsFresh) = await DrainAsync(fresh);
            Assert.Equal(cursorsBefore, cursorsFresh);
            Assert.Equal(payloadsBefore, payloadsFresh);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_dry_run_reports_the_same_counts_and_writes_nothing()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/dry", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            byte[] before = await File.ReadAllBytesAsync(ChangeLog(directory));

            ChangeLogCompactionResult dry = await store.CompactChangeLogAsync(dryRun: true);
            Assert.True(dry.DryRun);
            Assert.Equal(2, dry.LinesBefore);
            Assert.Equal(1, dry.LinesAfter);
            Assert.Equal(before, await File.ReadAllBytesAsync(ChangeLog(directory)));

            ChangeLogCompactionResult wet = await store.CompactChangeLogAsync();
            Assert.False(wet.DryRun);
            Assert.Equal(dry.LinesBefore, wet.LinesBefore);
            Assert.Equal(dry.LinesAfter, wet.LinesAfter);
            Assert.Equal(dry.BytesReclaimed, wet.BytesReclaimed);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Compaction_copies_an_unterminated_final_line_through_verbatim()
    {
        // The torn-tail invariant is load-bearing: a crash mid-append leaves the log ending in a
        // partial line, and that shape is what turns the crash into a loud, recoverable corruption
        // report instead of a silent loss. A compactor that "repaired" or dropped it would destroy
        // the evidence, and the append path's own newline check would then merge the next record
        // into it.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/torn", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });
            const string torn = "638000000000000000\tapp\tcompact";
            await File.AppendAllTextAsync(ChangeLog(directory), torn);

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();

            // The fragment is not a line, so it is not counted -- and it is still there, byte for byte.
            Assert.Equal(2, result.LinesBefore);
            Assert.Equal(1, result.LinesAfter);
            string log = await File.ReadAllTextAsync(ChangeLog(directory));
            Assert.EndsWith(torn, log, StringComparison.Ordinal);
            Assert.False(log.EndsWith('\n'));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Compaction_with_nothing_to_drop_leaves_the_log_byte_identical()
    {
        // Also the idempotence proof: a second compaction of an already-compacted log rewrites
        // nothing, which is what keeps repeat calls cheap and -- once Task 5 lands -- keeps a no-op
        // from bumping the generation and discarding every reader's index.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/clean", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            byte[] before = await File.ReadAllBytesAsync(ChangeLog(directory));

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();

            Assert.Equal(result.LinesBefore, result.LinesAfter);
            Assert.Equal(0, result.BytesReclaimed);
            Assert.Equal(before, await File.ReadAllBytesAsync(ChangeLog(directory)));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Compaction_on_a_store_that_has_never_written_a_log_reports_nothing()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();

            Assert.Equal(0, result.LinesBefore);
            Assert.Equal(0, result.LinesAfter);
            Assert.Equal(0, result.BytesBefore);
            Assert.False(File.Exists(ChangeLog(directory)));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Compaction_drops_the_import_residue_so_a_moved_revision_yields_once()
    {
        // The one place compaction changes what the feed yields, and it is a fix rather than a
        // side effect. Re-importing an existing revision at a DIFFERENT GlobalPosition rewrites the
        // history file in place while the log only ever grows, so the old line now dereferences to
        // the rewritten record: ReadAsync yields it twice, and the two envelopes disagree with
        // themselves -- the Cursor carries the old position while Record.GlobalPosition carries the
        // new one. Documented on three providers at docs/providers/index.md; this fixes the
        // filesystem one.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/residue", StatePartition.Default);

            await store.ImportAsync(Record(address, revision: 1, position: 101, value: "v1"));
            await store.ImportAsync(Record(address, revision: 1, position: 102, value: "v1"));

            (long[] cursorsBefore, _) = await DrainAsync(store);
            Assert.Equal([101L, 102L], cursorsBefore);

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();

            Assert.Equal(2, result.LinesBefore);
            Assert.Equal(1, result.LinesAfter);

            (long[] cursorsAfter, _) = await DrainAsync(store);
            Assert.Equal([102L], cursorsAfter);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_surviving_temp_file_neither_breaks_a_read_nor_a_later_compaction()
    {
        // Proof (b). A crash between the temp write and the rename leaves a
        // _changes.log.<guid>.tmp next to an intact log. The log is opened by exact name and nothing
        // globs the directory, so the read path must not notice, and a later compaction must
        // succeed. The stale temp is left alone: compaction's own finally deletes only the temp it
        // created, and deleting a file it did not write would be a repair, which this method is not.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/crash", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            string stale = ChangeLog(directory) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(stale, "this is a half-written compaction\n");

            (long[] cursorsBefore, _) = await DrainAsync(store);
            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();
            (long[] cursorsAfter, _) = await DrainAsync(store);

            Assert.Equal(1, result.LinesAfter);
            Assert.Equal(cursorsBefore, cursorsAfter);
            Assert.True(File.Exists(stale), "compaction deleted a temp file it did not create.");
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_reader_holding_the_log_open_the_way_this_provider_does_cannot_block_compaction()
    {
        // Proof (c). FileShare.ReadWrite | FileShare.Delete is exactly what this provider's three
        // change-log read opens now use, and FILE_SHARE_DELETE is what a POSIX-semantics rename
        // requires of every handle already open on the file it replaces.
        //
        // Measured, not assumed: on Windows, File.Move(..., overwrite: true) --
        // MoveFileEx(MOVEFILE_REPLACE_EXISTING) -- fails with ERROR_ACCESS_DENIED against a
        // destination ANY process holds open, whether or not that handle granted FILE_SHARE_DELETE.
        // The classic replace has to take the destination's name out of the directory, and while a
        // handle is open the file system can only mark the file delete-pending and leave the name
        // where it is. ReplaceFileOverOpenReaders therefore renames with
        // FILE_RENAME_FLAG_POSIX_SEMANTICS, which detaches the name immediately -- what rename(2) has
        // always done, and what this provider's readers already assume.
        //
        // BREAK THE MECHANISM: drop `| FileShare.Delete` from the held open below, rebuild, and run
        // this on Windows. The compaction throws
        //
        //   System.IO.IOException : Could not replace '<dir>\_changes.log': The process cannot
        //   access the file because it is being used by another process.
        //
        // Restore it before committing and record the exact message in the task report. On Linux and
        // macOS this test passes either way, because rename(2) does not consult share modes at all,
        // so this is a Windows proof and the report must say so rather than claim a cross-platform one.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/shared", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            byte[] before = await File.ReadAllBytesAsync(ChangeLog(directory));

            await using (var held = new FileStream(
                ChangeLog(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                ChangeLogCompactionResult result = await store.CompactChangeLogAsync();
                Assert.True(result.BytesReclaimed > 0);

                // The held handle keeps reading the file it opened: the unlinked-file behaviour this
                // provider's readers already assume, which Windows gives once the rename carries
                // POSIX semantics and every open handle granted FILE_SHARE_DELETE.
                byte[] stillReadable = new byte[before.Length];
                await held.ReadExactlyAsync(stillReadable);
                Assert.Equal(before, stillReadable);

                // ... and the NAME now resolves to the compacted file. Together with the assertion
                // above, that is what makes this a rename of a directory entry rather than an edit of
                // the bytes the held handle points at.
                byte[] byName = await File.ReadAllBytesAsync(ChangeLog(directory));
                Assert.NotEqual(before, byName);
                Assert.Equal(result.BytesAfter, byName.Length);
            }

            await using var fresh = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            (long[] cursors, _) = await DrainAsync(fresh);
            Assert.Single(cursors);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Compaction_throws_on_a_malformed_line_that_is_not_the_last()
    {
        // Same verdict the read path gives, and for the same reason: only the FINAL line of the log
        // can be a partially-written append, so a malformed line before the end means the log is
        // corrupt. A compactor that silently dropped it could be discarding a real record.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "compact/corrupt", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await File.AppendAllTextAsync(ChangeLog(directory), "not-a-position\tapp\n");
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            InvalidDataException thrown =
                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.CompactChangeLogAsync());

            Assert.Contains("line 2", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("_changes.log", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string ChangeLog(string directory) => Path.Combine(directory, "_changes.log");

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(long[] Cursors, string[] Payloads)> DrainAsync(IStateChangeFeed feed)
    {
        var cursors = new List<long>();
        var payloads = new List<string>();
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from: null, StateChangeReadOptions.Default))
        {
            cursors.Add(envelope.Cursor.Position);
            payloads.Add(Encoding.UTF8.GetString(envelope.Record.Payload ?? []));
        }

        return ([.. cursors], [.. payloads]);
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static StateRecord Record(StateAddress address, long revision, long position, string value) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
