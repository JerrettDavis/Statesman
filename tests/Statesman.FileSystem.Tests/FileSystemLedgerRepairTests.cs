using System.Text;
using System.Text.Json;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// What repair is allowed to do to each finding. The governing rule is that repair never destroys data
/// verify could not prove orphaned, so only two kinds are dropped, two are written back from data
/// already on disk, one is quarantined by rename, and four are reported and left exactly alone.
/// </summary>
public sealed class FileSystemLedgerRepairTests
{
    [Fact]
    public async Task The_parameterless_overload_is_a_dry_run_and_changes_nothing()
    {
        // Deliberately the opposite default from CompactChangeLogAsync(), whose parameterless overload
        // applies: compaction only removes lines it has proved dereference to nothing, where repair
        // renames and writes files. Pre-Phase-18 addendum decision 82.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/dryrun", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            File.Delete(HeadFiles(directory).Single());

            (string Path, long Length, DateTime Written)[] before = Snapshot(directory);
            FileSystemLedgerRepairReport report = await store.RepairAsync();
            (string Path, long Length, DateTime Written)[] after = Snapshot(directory);

            Assert.True(report.DryRun);
            Assert.Equal(1, report.HeadsRewritten);
            Assert.Equal(before, after);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_missing_head_is_written_back_from_the_newest_history_file()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/headless", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string head = HeadFiles(directory).Single();
            File.Delete(head);
            Assert.Empty(await DrainPartitionsAsync(store));

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.False(report.DryRun);
            Assert.Equal(1, report.HeadsRewritten);
            Assert.True(File.Exists(head));

            // The stream is visible to the catalog again, which is the data loss this closes ...
            StatePartitionDescriptor descriptor = Assert.Single(await DrainPartitionsAsync(store));
            Assert.Equal(address, descriptor.Address);

            // ... verify is clean ...
            Assert.Empty((await store.VerifyAsync()).Findings);

            // ... and the store still appends on top of the restored head.
            StateAppendResult result = await store.AppendAsync(
                address, StateWriteCondition.AtRevision(2), Commit("v3"));
            Assert.True(result.Succeeded);
            Assert.Equal(3, result.Record!.Revision);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_missing_history_file_is_written_back_from_the_head()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/headonly", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string history = HistoryDirectories(directory).Single();
            string newest = Directory.GetFiles(history, "*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(newest);

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.Equal(1, report.HistoryFilesRestored);
            Assert.True(File.Exists(newest));

            StateRecord? restored = await store.ReadLatestAsync(address);
            Assert.NotNull(restored);
            Assert.Equal(2, restored!.Revision);
            Assert.Equal("v2", Encoding.UTF8.GetString(restored.Payload!));

            StateAppendResult result = await store.AppendAsync(
                address, StateWriteCondition.AtRevision(2), Commit("v3"));
            Assert.True(result.Succeeded);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task An_unreadable_record_file_is_quarantined_by_rename_and_never_deleted()
    {
        // The rename is the whole cure: "*.json" does not match a ".corrupt" suffix, so the file leaves
        // every read path's enumeration while its bytes stay on disk for an operator. Measured on
        // .NET 10 on Windows before this was planned.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/unreadable", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string history = HistoryDirectories(directory).Single();
            string first = Directory.GetFiles(history, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            await File.WriteAllTextAsync(first, "this is not json");

            // Before: one bad file throws for the whole unbounded feed.
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (StateChangeEnvelope _ in store.ReadAsync(
                    from: null, StateChangeReadOptions.Default))
                {
                }
            });

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.Equal(1, report.RecordFilesQuarantined);
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(first + ".corrupt"));
            Assert.Equal("this is not json", await File.ReadAllTextAsync(first + ".corrupt"));

            // After: the feed reads. The quarantined revision is gone from it, which is honest -- its
            // record was unreadable -- and the store still appends.
            List<StateChangeEnvelope> drained = await DrainAsync(store);
            Assert.Equal([2L], drained.Select(envelope => envelope.Record.Revision).ToArray());
            StateAppendResult result = await store.AppendAsync(
                address, StateWriteCondition.AtRevision(2), Commit("v3"));
            Assert.True(result.Succeeded);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_second_quarantine_of_the_same_name_gets_a_numbered_suffix()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/twice", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string history = HistoryDirectories(directory).Single();
            string first = Directory.GetFiles(history, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();

            await File.WriteAllTextAsync(first, "damaged once");
            await store.RepairAsync(dryRun: false);
            await File.WriteAllTextAsync(first, "damaged twice");
            await store.RepairAsync(dryRun: false);

            Assert.Equal("damaged once", await File.ReadAllTextAsync(first + ".corrupt"));
            Assert.Equal("damaged twice", await File.ReadAllTextAsync(first + ".corrupt.1"));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_torn_tail_a_temporary_file_and_a_misplaced_directory_are_reported_unrepaired()
    {
        // Three of the four "repair does nothing" kinds in one store, each for its own reason: the torn
        // tail is the crash marker, a temporary file is indistinguishable from a live in-flight write,
        // and moving a directory cannot be proved safe. They appear in Unrepaired and on disk unchanged.
        //
        // The misplaced directory is a COPY of the canonical stream, not a move: the canonical stream
        // must stay in place so the one change-log line the store wrote for it still resolves to a
        // readable history file. A move would also make that line's history file vanish from its
        // canonical, hash-derived path, producing an extra DanglingChangeLogLine finding -- a genuinely
        // correct finding under VerifyChangeLogUnsafeAsync's absent-vs-unreadable rule, but not one this
        // fixture is trying to exercise, and one Task 4's compaction would act on, breaking the
        // disk-unchanged assertion below.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/untouched", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            string temp = ChangeLog(directory) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, "half a compaction\n");
            await File.AppendAllTextAsync(ChangeLog(directory), "638000000000000000\tapp\trepair");
            string stream = Path.GetDirectoryName(HeadFiles(directory).Single())!;
            string wrong = Path.Combine(directory, "ff", "ff" + new string('0', 62));
            Directory.CreateDirectory(Path.Combine(directory, "ff"));
            CopyDirectory(stream, wrong);

            (string Path, long Length, DateTime Written)[] before = Snapshot(directory);
            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);
            (string Path, long Length, DateTime Written)[] after = Snapshot(directory);

            // Ascending by Kind's underlying int, matching the .OrderBy(kind => kind) applied to the
            // actual side just below: TornChangeLogTail = 0, OrphanedTemporaryFile = 7,
            // MisplacedStreamDirectory = 8.
            Assert.Equal(
                [
                    FileSystemLedgerFindingKind.TornChangeLogTail,
                    FileSystemLedgerFindingKind.OrphanedTemporaryFile,
                    FileSystemLedgerFindingKind.MisplacedStreamDirectory,
                ],
                report.Unrepaired.Select(f => f.Kind).OrderBy(kind => kind).ToArray());
            Assert.Equal(0, report.HeadsRewritten);
            Assert.Equal(0, report.HistoryFilesRestored);
            Assert.Equal(0, report.RecordFilesQuarantined);
            Assert.Equal(before, after);
        }
        finally
        {
            Delete(directory);
        }
    }

    // A recursive copy, not Directory.Move: the caller needs the source stream to stay in place. See the
    // comment at the one call site.
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string subdirectory in Directory.GetDirectories(source))
        {
            CopyDirectory(subdirectory, Path.Combine(destination, Path.GetFileName(subdirectory)));
        }
    }

    private static (string Path, long Length, DateTime Written)[] Snapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)))
            .ToArray();

    private static string ChangeLog(string directory) => Path.Combine(directory, "_changes.log");

    private static string[] HeadFiles(string directory) =>
        Directory.GetFiles(directory, "head.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string[] HistoryDirectories(string directory) =>
        Directory.GetDirectories(directory, "history", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static async Task<List<StateChangeEnvelope>> DrainAsync(FileSystemStateLedgerStore store)
    {
        var drained = new List<StateChangeEnvelope>();
        await foreach (StateChangeEnvelope envelope in store.ReadAsync(
            from: null, StateChangeReadOptions.Default))
        {
            drained.Add(envelope);
        }

        return drained;
    }

    private static async Task<List<StatePartitionDescriptor>> DrainPartitionsAsync(
        FileSystemStateLedgerStore store)
    {
        var drained = new List<StatePartitionDescriptor>();
        await foreach (StatePartitionDescriptor descriptor in store.ListPartitionsAsync())
        {
            drained.Add(descriptor);
        }

        return drained;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Refreshed,
        Status = StateStatus.Ready,
        ValueType = "test",
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
