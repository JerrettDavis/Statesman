using System.Text;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// The <c>_changes.gen</c> compaction generation: the witness that makes it necessary, the
/// cross-instance agreement it guarantees, and the two things it must not do.
/// </summary>
public sealed class FileSystemChangeLogGenerationTests
{
    // Both three digits, so every line this file writes has the same width -- which is what makes
    // the byte-offset arithmetic in the cross-instance test exact rather than approximate.
    private const long PositionOne = 101;
    private const long PositionTwo = 102;

    [Fact]
    public async Task A_ping_pong_import_writes_two_byte_identical_change_log_lines()
    {
        // The witness mechanism, stated as a test because the design rests on it and because the
        // obvious version of it is wrong. ImportAsync appends a change-log line only when the
        // incoming position differs from the one the existing revision file already carries, so
        // importing the SAME revision twice at the SAME position appends one line, not two. What
        // produces two byte-identical lines is a ping-pong: 101, then 102 (which rewrites the
        // history file), then 101 again (which differs from 102, so it appends) -- and that third
        // line matches the first byte for byte.
        //
        // Two identical lines are what would let a compaction shift one into the other's byte offset
        // and leave a partially-scanned reader's tail re-verify passing on stale content. They are
        // why CompactChangeLogAsync collapses duplicates, and why the generation counter exists.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/a", StatePartition.Default);

            await store.ImportAsync(Record(address, PositionOne));
            await store.ImportAsync(Record(address, PositionTwo));
            await store.ImportAsync(Record(address, PositionOne));

            string[] lines = (await File.ReadAllTextAsync(ChangeLog(directory)))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(3, lines.Length);
            Assert.Equal(lines[0], lines[2]);
            Assert.NotEqual(lines[0], lines[1]);
            Assert.Equal(lines[0].Length, lines[1].Length);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_ping_pong_log_compacts_to_one_line()
    {
        // Exercises the compactor's `seen` dedup branch directly. The same five-import ping-pong as
        // the witness test above produces three byte-identical PositionOne lines and two PositionTwo
        // residue lines; compaction must drop the residue AND collapse the duplicates down to the one
        // surviving line, rather than merely keeping every line whose position still matches.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/a", StatePartition.Default);

            await store.ImportAsync(Record(address, PositionOne));
            await store.ImportAsync(Record(address, PositionTwo));
            await store.ImportAsync(Record(address, PositionOne));
            await store.ImportAsync(Record(address, PositionTwo));
            await store.ImportAsync(Record(address, PositionOne));

            string firstLineBeforeCompaction = (await File.ReadAllTextAsync(ChangeLog(directory)))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();
            Assert.Equal(5, result.LinesBefore);
            Assert.Equal(1, result.LinesAfter);

            string[] linesAfterCompaction = (await File.ReadAllTextAsync(ChangeLog(directory)))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(linesAfterCompaction);
            Assert.Equal(firstLineBeforeCompaction, linesAfterCompaction[0]);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_second_instance_agrees_with_a_fresh_one_after_a_compaction_it_did_not_perform()
    {
        // The cross-process case, modelled by two store instances over one directory in one process:
        // their change-feed gates are separate, so neither serializes against the other, which is
        // exactly the relationship two processes have. (FileSystemChangeFeedIndexTests already uses
        // this technique for the append case.)
        //
        // The reader indexes the log at three lines, two more are appended, and then the writer
        // compacts. Whatever the reader serves afterwards must equal what an instance that has never
        // seen the old file serves. See the task's four-state table for which shipped rule closes
        // which state.
        string directory = TempDirectory();
        try
        {
            await using var writer = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/a", StatePartition.Default);

            await writer.ImportAsync(Record(address, PositionOne));
            await writer.ImportAsync(Record(address, PositionTwo));
            await writer.ImportAsync(Record(address, PositionOne));

            await using var reader = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            Assert.Equal(3, (await DrainAsync(reader)).Length);

            await writer.ImportAsync(Record(address, PositionTwo));
            await writer.ImportAsync(Record(address, PositionOne));

            // The history file now carries PositionOne, so every PositionTwo line is residue and
            // every surviving line is a duplicate of every other.
            ChangeLogCompactionResult result = await writer.CompactChangeLogAsync();
            Assert.Equal(5, result.LinesBefore);

            await using var fresh = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            // The witness: with the generation check removed AND duplicate collapse removed, the
            // reader's index survives the compaction on stale offsets and this comparison fails;
            // either rule alone keeps it passing (see the four-state table in the task report).
            Assert.Equal(await DrainAsync(fresh), await DrainAsync(reader));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_directory_with_no_generation_file_still_reads_its_log_incrementally()
    {
        // The compatibility proof for every store directory written before this phase: a missing
        // sidecar reads as zero, which equals the field's initial value, so nothing is discarded and
        // the index still serves an incremental read. A generation check that treated "missing" as
        // "changed" would silently turn every read back into a whole-log parse.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/plain", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            long[] first = await DrainAsync(store);
            Assert.Single(first);
            Assert.False(File.Exists(GenerationFile(directory)));

            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            long[] second = await DrainAsync(store, from: new StateChangeCursor(first[0]));
            Assert.Single(second);
            Assert.False(File.Exists(GenerationFile(directory)));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_corrupt_generation_file_reads_as_zero_and_the_log_still_reads_incrementally()
    {
        // ReadChangeFeedGenerationUnsafeAsync treats a missing, empty OR unparseable sidecar as zero.
        // This is the unparseable case: a `_changes.gen` that exists but holds garbage reads as zero,
        // which equals the field's initial value, so nothing is discarded and the index still serves
        // an incremental read -- same shape and same assertions as
        // A_directory_with_no_generation_file_still_reads_its_log_incrementally, but with the sidecar
        // present and corrupt rather than absent.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/corrupt", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            await File.WriteAllTextAsync(GenerationFile(directory), "not a number\n");

            long[] first = await DrainAsync(store);
            Assert.Single(first);
            Assert.Equal("not a number\n", await File.ReadAllTextAsync(GenerationFile(directory)));

            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            long[] second = await DrainAsync(store, from: new StateChangeCursor(first[0]));
            Assert.Single(second);
            Assert.Equal("not a number\n", await File.ReadAllTextAsync(GenerationFile(directory)));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_compaction_with_nothing_to_drop_does_not_create_the_generation_file()
    {
        // Raising the generation discards every reader's index, so a no-op must not do it. This is
        // also what makes a maintenance schedule that compacts a clean log every hour free rather
        // than a scheduled whole-log re-parse in every process.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/noop", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            await store.CompactChangeLogAsync(dryRun: true);
            Assert.False(File.Exists(GenerationFile(directory)));

            ChangeLogCompactionResult result = await store.CompactChangeLogAsync();
            Assert.Equal(0, result.BytesReclaimed);
            Assert.False(File.Exists(GenerationFile(directory)));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_compaction_that_drops_a_line_raises_the_generation_each_time()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/bump", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            await store.CompactChangeLogAsync();
            Assert.Equal("1", (await File.ReadAllTextAsync(GenerationFile(directory))).Trim());

            await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("v3"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });
            await store.CompactChangeLogAsync();
            Assert.Equal("2", (await File.ReadAllTextAsync(GenerationFile(directory))).Trim());
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_reader_holding_the_generation_open_the_way_this_provider_does_cannot_block_a_bump()
    {
        // The sidecar's own proof (c). The seqlock opens _changes.gen with
        // FileShare.ReadWrite | FileShare.Delete twice per ReadAsync, so this name is held open more
        // often than the log is, and a bump has to be able to replace it underneath that handle.
        //
        // Measured, not assumed: on Windows, File.Move(..., overwrite: true) --
        // MoveFileEx(MOVEFILE_REPLACE_EXISTING) -- fails with ERROR_ACCESS_DENIED against a
        // destination ANY process holds open, whether or not that handle granted FILE_SHARE_DELETE.
        // ReplaceFileOverOpenReaders renames with FILE_RENAME_FLAG_POSIX_SEMANTICS instead, which
        // detaches the name immediately and leaves the open handle reading the file it opened.
        //
        // BREAK THE MECHANISM: drop `| FileShare.Delete` from the held open below, rebuild, and run
        // this on Windows. The bump throws
        //
        //   System.IO.IOException : Could not replace '<dir>\_changes.gen': The process cannot
        //   access the file because it is being used by another process.
        //
        // Restore it before committing. On Linux and macOS this test passes either way, because
        // rename(2) does not consult share modes at all, so this is a Windows proof.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "gen/held", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            // The first compaction creates the sidecar; only then is there a file to hold open
            // across the second one.
            await store.CompactChangeLogAsync();
            Assert.Equal("1", (await File.ReadAllTextAsync(GenerationFile(directory))).Trim());

            await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("v3"));
            await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

            await using (var held = new FileStream(
                GenerationFile(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                ChangeLogCompactionResult result = await store.CompactChangeLogAsync();
                Assert.True(result.BytesReclaimed > 0);

                // The name now resolves to the raised generation ...
                Assert.Equal("2", (await File.ReadAllTextAsync(GenerationFile(directory))).Trim());

                // ... while the held handle keeps reading the generation it opened. That pair is what
                // makes this a rename of a directory entry rather than an edit of the bytes the held
                // handle points at.
                byte[] stillReadable = new byte[2];
                int read = await held.ReadAsync(stillReadable);
                Assert.Equal("1", Encoding.UTF8.GetString(stillReadable, 0, read).Trim());
            }
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string ChangeLog(string directory) => Path.Combine(directory, "_changes.log");

    private static string GenerationFile(string directory) => Path.Combine(directory, "_changes.gen");

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

    private static async Task<long[]> DrainAsync(IStateChangeFeed feed, StateChangeCursor? from = null)
    {
        var cursors = new List<long>();
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from, StateChangeReadOptions.Default))
        {
            cursors.Add(envelope.Cursor.Position);
        }

        return [.. cursors];
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

    private static StateRecord Record(StateAddress address, long position) => new()
    {
        Address = address,
        Revision = 1,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes("v1"),
        Source = "test",
    };
}
