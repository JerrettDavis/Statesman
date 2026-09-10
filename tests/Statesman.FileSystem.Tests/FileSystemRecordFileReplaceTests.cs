using System.Text;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// Head and history files are replaced and deleted by name while a reader holds them open — the
/// window a feed read opens over every history file it yields.
/// </summary>
public sealed class FileSystemRecordFileReplaceTests
{
    [Fact]
    public async Task A_reader_holding_a_history_file_the_way_this_provider_does_cannot_block_a_reimport()
    {
        // The in-process window, made deterministic. ReadAsync reads each history file through
        // ReadFileAsync OUTSIDE both gates, while ImportAsync replaces that same file in place under
        // Gate(address) + _changeFeedGate. This test holds the file open with the share mode
        // ReadFileAsync uses and drives the import, which is the same overlap without the race.
        //
        // Measured, not assumed: on Windows, File.Move(..., overwrite: true) --
        // MoveFileEx(MOVEFILE_REPLACE_EXISTING) -- fails with ERROR_ACCESS_DENIED against a
        // destination ANY process holds open, whether or not that handle granted FILE_SHARE_DELETE.
        // AtomicWriteAsync therefore replaces through ReplaceFileOverOpenReaders, which renames with
        // FILE_RENAME_FLAG_POSIX_SEMANTICS and leaves the open handle reading the file it opened.
        //
        // BREAK THE MECHANISM: drop `| FileShare.Delete` from the held open below, rebuild, and run
        // this on Windows. The import throws an IOException naming the history file. On Linux and
        // macOS this test passes either way, because rename(2) does not consult share modes at all,
        // so this is a Windows proof.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "replace/history", StatePartition.Default);

            await store.ImportAsync(Record(address, revision: 1, position: 101, "v1"));

            string historyFile = HistoryFiles(directory).Single();
            byte[] before = await File.ReadAllBytesAsync(historyFile);

            await using (var held = new FileStream(
                historyFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                // A re-import at a different position rewrites the revision file in place. This is
                // the only write path that replaces a history file whose name already exists;
                // AppendAsync always writes a revision nobody can be holding yet.
                await store.ImportAsync(Record(address, revision: 1, position: 102, "v2"));

                // The name now resolves to the rewritten record ...
                byte[] byName = await File.ReadAllBytesAsync(historyFile);
                Assert.NotEqual(before, byName);

                // ... while the held handle keeps reading the record it opened. That pair is what
                // makes this a rename of a directory entry rather than an edit of the bytes the held
                // handle points at, which is what stops a feed read mid-deserialize from seeing a
                // torn record.
                byte[] stillReadable = new byte[before.Length];
                await held.ReadExactlyAsync(stillReadable);
                Assert.Equal(before, stillReadable);
            }

            StateRecord? latest = await store.ReadLatestAsync(address);
            Assert.NotNull(latest);
            Assert.Equal(102, latest!.GlobalPosition);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_reader_holding_the_head_file_the_way_this_provider_does_cannot_block_an_append()
    {
        // The same primitive on the hot path. Every AppendAsync replaces head.json through
        // AtomicWriteAsync, so a reader holding head.json open -- ReadLatestAsync in a second process,
        // which is the shape a replica poller has -- would fail this process's writer on Windows.
        //
        // BREAK THE MECHANISM: drop `| FileShare.Delete` from the held open below and run on Windows.
        // The append throws an IOException naming head.json. Passes either way on Unix.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "replace/head", StatePartition.Default);

            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            string headFile = Directory
                .GetFiles(directory, "head.json", SearchOption.AllDirectories)
                .Single();
            byte[] before = await File.ReadAllBytesAsync(headFile);

            await using (var held = new FileStream(
                headFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

                byte[] byName = await File.ReadAllBytesAsync(headFile);
                Assert.NotEqual(before, byName);

                byte[] stillReadable = new byte[before.Length];
                await held.ReadExactlyAsync(stillReadable);
                Assert.Equal(before, stillReadable);
            }

            StateRecord? latest = await store.ReadLatestAsync(address);
            Assert.NotNull(latest);
            Assert.Equal(2, latest!.Revision);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_reader_holding_a_history_file_the_way_this_provider_does_cannot_block_a_prune()
    {
        // A GUARD, NOT A PROOF -- and the report must say so. This test passes before and after this
        // task's production change, because it holds the file with FileShare.Delete itself. What it
        // guards is the OTHER half of the change: PruneAsync deletes history files by name, and
        // File.OpenRead -- what ReadFileAsync used before this task -- grants no FILE_SHARE_DELETE, so
        // a prune concurrent with a feed read fails on Windows today with
        // "The process cannot access the file ... because it is being used by another process."
        //
        // BREAK THE MECHANISM: drop `| FileShare.Delete` from the held open below and run on Windows.
        // PruneAsync throws that IOException. That failure IS the defect ReadFileAsync's widened share
        // mode removes; it is the closest demonstration available, because no seam in this provider
        // holds a store-opened handle across a call -- ReadFileAsync opens and closes inside one
        // method, and ReadHistoryAsync loads every record before it yields any.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "replace/prune", StatePartition.Default);

            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("v3"));

            // One address means one history directory whose files are named by revision, so ordinal
            // name order is revision order -- the same idiom FileSystemChangeFeedTests uses.
            string first = HistoryFiles(directory).First();
            byte[] before = await File.ReadAllBytesAsync(first);

            await using (var held = new FileStream(
                first, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                await store.PruneAsync(address, new StateRetentionPolicy { MaxRevisions = 1 });

                Assert.False(File.Exists(first), "the pruned revision's file is still in the directory.");

                byte[] stillReadable = new byte[before.Length];
                await held.ReadExactlyAsync(stillReadable);
                Assert.Equal(before, stillReadable);
            }

            Assert.Single(HistoryFiles(directory));
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string[] HistoryFiles(string directory) =>
        [.. Directory
            .GetFiles(
                Directory.GetDirectories(directory, "history", SearchOption.AllDirectories).Single(),
                "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)];

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
