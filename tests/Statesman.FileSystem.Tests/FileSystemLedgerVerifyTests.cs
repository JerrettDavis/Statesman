using System.Text;
using System.Text.Json;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// What a report-only verify pass finds in the change log. Every damaged file below is damaged in the
/// crash-durable byte shape the writer actually produces: the log is append-only and written one whole
/// line at a time, so only its FINAL line can be a partial write, and every earlier line was written
/// after its predecessor's append had completed.
/// </summary>
public sealed class FileSystemLedgerVerifyTests
{
    [Fact]
    public async Task A_healthy_store_reports_no_findings_and_counts_every_line()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/clean", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            Assert.Empty(report.Findings);
            Assert.Equal(0, report.FindingCount);
            Assert.Equal(2, report.ChangeLogLinesScanned);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_torn_final_line_is_reported_and_the_store_still_appends()
    {
        // The crash-durable shape: the log's append is not atomic, so a crash mid-append leaves the
        // file ending in a prefix of a real line with no terminating newline. That is the ONLY partial
        // line a crash can produce.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/torn", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await File.AppendAllTextAsync(ChangeLog(directory), "638000000000000000\tapp\tverify");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(report.Findings);
            Assert.Equal(FileSystemLedgerFindingKind.TornChangeLogTail, finding.Kind);
            Assert.Equal(ChangeLog(directory), finding.Path);
            Assert.Equal(1, report.ChangeLogLinesScanned);

            // ... and verify wrote nothing: a subsequent append still succeeds, and the writer's own
            // separator rule turns the torn prefix into a malformed line that is no longer last.
            StateAppendResult result = await store.AppendAsync(
                address, StateWriteCondition.AtRevision(1), Commit("v2"));
            Assert.True(result.Succeeded);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_terminated_but_unparseable_final_line_is_reported_as_a_torn_tail()
    {
        // Every read path tolerates an unparseable FINAL line and compaction copies it through, so it
        // gets the same kind and the same "repair does nothing" treatment as an unterminated one. The
        // kind names the tail, not the mechanism.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/final", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await File.AppendAllTextAsync(ChangeLog(directory), "not-a-position\tapp\n");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(report.Findings);
            Assert.Equal(FileSystemLedgerFindingKind.TornChangeLogTail, finding.Kind);
            Assert.Equal(2, report.ChangeLogLinesScanned);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_malformed_line_before_the_end_is_reported_with_its_one_based_number()
    {
        // Only the final line can be a partially-written append, so a malformed line before the end
        // means the log is corrupt -- and ReadAsync THROWS on it rather than degrading. Verify reports
        // it with the same 1-based number the read path's exception names.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/malformed", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            string log = ChangeLog(directory);
            string[] lines = await File.ReadAllLinesAsync(log);
            await File.WriteAllTextAsync(log, lines[0] + "\n" + "garbage\n" + lines[1] + "\n");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(report.Findings);
            Assert.Equal(FileSystemLedgerFindingKind.MalformedChangeLogLine, finding.Kind);
            Assert.Equal(2L, finding.ChangeLogLine);
            Assert.Equal(3, report.ChangeLogLinesScanned);

            // The read path's verdict on the same file, for comparison: it throws naming line 2.
            InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (StateChangeEnvelope _ in store.ReadAsync(
                    from: null, StateChangeReadOptions.Default))
                {
                }
            });
            Assert.Contains("line 2", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_line_whose_history_file_is_gone_is_reported_as_dangling()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/dangling", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            // Delete revision 1's history file by hand, which is the shape PruneAsync leaves behind,
            // produced here so the test does not depend on which files a policy happens to keep.
            string history = Directory
                .GetDirectories(directory, "history", SearchOption.AllDirectories)
                .Single();
            string first = Directory.GetFiles(history, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(first);

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.DanglingChangeLogLine);
            Assert.Equal(first, finding.Path);
            Assert.Equal(address, finding.Address);
            Assert.Equal(1L, finding.Revision);
            Assert.Equal(1L, finding.ChangeLogLine);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_line_whose_history_file_moved_position_is_reported_as_a_mismatch()
    {
        // The import residue: re-importing an existing revision at a different GlobalPosition rewrites
        // the history file in place while the append-only log keeps the old line, so the record yields
        // TWICE and one envelope's Cursor disagrees with its own Record.GlobalPosition.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/moved", StatePartition.Default);
            await store.ImportAsync(Record(address, revision: 1, position: 101, "v1"));
            await store.ImportAsync(Record(address, revision: 1, position: 102, "v2"));

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.ChangeLogPositionMismatch);
            Assert.Equal(address, finding.Address);
            Assert.Equal(1L, finding.Revision);
            Assert.Equal(1L, finding.ChangeLogLine);
            Assert.Contains("101", finding.Detail, StringComparison.Ordinal);
            Assert.Contains("102", finding.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Verify_writes_nothing_at_all()
    {
        // Report-only is a promise about bytes, so it is measured in bytes: every file under the root,
        // with its length and last-write time, before and after.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/readonly", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await File.AppendAllTextAsync(ChangeLog(directory), "638000000000000000\tapp\tverify");

            (string Path, long Length, DateTime Written)[] before = Snapshot(directory);
            FileSystemLedgerVerificationReport report = await store.VerifyAsync();
            (string Path, long Length, DateTime Written)[] after = Snapshot(directory);

            Assert.NotEmpty(report.Findings);
            Assert.Equal(before, after);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_stream_with_history_and_no_head_is_reported_and_is_invisible_to_the_catalog()
    {
        // Not cosmetic: ListPartitionsAsync gates every candidate on File.Exists(head), so a headless
        // stream disappears from the partition catalog -- and Statesman.Tooling enumerates exactly that
        // catalog when it exports. Every record is still on disk and still readable by address.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/headless", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            string head = HeadFiles(directory).Single();
            File.Delete(head);

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(report.Findings);
            Assert.Equal(FileSystemLedgerFindingKind.MissingHead, finding.Kind);
            Assert.Equal(head, finding.Path);
            Assert.Equal(address, finding.Address);
            Assert.Equal(1, report.StreamsScanned);
            Assert.Equal(2, report.RecordFilesScanned);

            // The two halves of why this matters, both measured here rather than asserted in prose.
            StateRecord? latest = await store.ReadLatestAsync(address);
            Assert.NotNull(latest);
            Assert.Equal(2, latest!.Revision);
            Assert.Empty(await DrainPartitionsAsync(store));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_head_whose_revision_has_no_history_file_is_reported()
    {
        // Pruning cannot produce this: every retention filter exempts the latest revision, so a head
        // with no history twin is genuinely anomalous rather than a normal retention outcome.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/headonly", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            string history = HistoryDirectories(directory).Single();
            string newest = Directory.GetFiles(history, "*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(newest);

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding missing = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.MissingHistoryFile);
            Assert.Equal(newest, missing.Path);
            Assert.Equal(address, missing.Address);
            Assert.Equal(2L, missing.Revision);

            // The same deletion leaves a dangling change-log line, and the stream walk runs first.
            Assert.Equal(
                [FileSystemLedgerFindingKind.MissingHistoryFile, FileSystemLedgerFindingKind.DanglingChangeLogLine],
                report.Findings.Select(f => f.Kind).ToArray());
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task An_unreadable_record_file_is_reported_and_is_not_mistaken_for_a_missing_one()
    {
        // The crash-durable shape for a record file is NOT a truncated one: AtomicWriteAsync writes a
        // temp and renames, so a crash leaves either the old file or the new one. A record file is
        // damaged by whole-file replacement, which is what an external process or a bad restore does.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/unreadable", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            string history = HistoryDirectories(directory).Single();
            string first = Directory.GetFiles(history, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            await File.WriteAllTextAsync(first, "this is not json");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.UnreadableRecordFile);
            Assert.Equal(first, finding.Path);

            // It is NOT reported as dangling: the file exists, so its change-log line must not be
            // treated as a line repair may drop.
            Assert.DoesNotContain(
                FileSystemLedgerFindingKind.DanglingChangeLogLine,
                report.Findings.Select(f => f.Kind));

            // And this is what it costs today: one bad file throws for the whole unbounded feed.
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
            {
                await foreach (StateChangeEnvelope _ in store.ReadAsync(
                    from: null, StateChangeReadOptions.Default))
                {
                }
            });
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task An_unreadable_newest_history_file_is_not_also_reported_as_missing()
    {
        // Lever B's discriminator: damaging the NEWEST history file (the head's own revision) rather
        // than an older one. The exists-versus-loaded rule must key off File.Exists, not off whether
        // the record loaded into memory -- a damaged-but-present file is not a missing one.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/unreadable-newest", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));

            string history = HistoryDirectories(directory).Single();
            string newest = Directory.GetFiles(history, "*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .First();
            await File.WriteAllTextAsync(newest, "this is not json");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.UnreadableRecordFile);
            Assert.Equal(newest, finding.Path);
            Assert.DoesNotContain(
                FileSystemLedgerFindingKind.MissingHistoryFile,
                report.Findings.Select(f => f.Kind));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task An_orphaned_temporary_file_is_reported_in_the_root_and_in_a_stream()
    {
        // A crash between AtomicWriteAsync's temp write and its rename leaves <file>.<32 hex>.tmp.
        // Invisible to every read path, because "*.json" does not match it -- measured, not assumed.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/temps", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            string rootTemp = ChangeLog(directory) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(rootTemp, "half a compaction\n");
            string history = HistoryDirectories(directory).Single();
            string historyTemp = Path.Combine(
                history, "00000000000000000002.json." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(historyTemp, "half a record\n");

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            Assert.Equal(
                [historyTemp, rootTemp],
                report.Findings
                    .Where(f => f.Kind == FileSystemLedgerFindingKind.OrphanedTemporaryFile)
                    .Select(f => f.Path)
                    .ToArray());

            // Still invisible to the reader, which is why they accumulate unnoticed.
            Assert.Single(await DrainAsync(store));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_stream_directory_under_the_wrong_hash_is_reported()
    {
        // Reachable only by a directory walk. No read by address will ever find it, because the
        // directory name is the SHA-256 of the address and this one is not.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "verify/misplaced", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));

            string stream = Path.GetDirectoryName(HeadFiles(directory).Single())!;
            string wrong = Path.Combine(
                directory,
                "ff",
                "ff" + new string('0', 62));
            Directory.CreateDirectory(Path.Combine(directory, "ff"));
            Directory.Move(stream, wrong);

            FileSystemLedgerVerificationReport report = await store.VerifyAsync();

            FileSystemLedgerFinding finding = Assert.Single(
                report.Findings, f => f.Kind == FileSystemLedgerFindingKind.MisplacedStreamDirectory);
            Assert.Equal(wrong, finding.Path);
            Assert.Equal(address, finding.Address);

            // The read by address finds nothing, which is the whole point of reporting it.
            Assert.Null(await store.ReadLatestAsync(address));
        }
        finally
        {
            Delete(directory);
        }
    }

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

    private static (string Path, long Length, DateTime Written)[] Snapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)))
            .ToArray();

    private static string ChangeLog(string directory) => Path.Combine(directory, "_changes.log");

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Refreshed,
        Status = StateStatus.Ready,
        ValueType = "test",
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static StateRecord Record(StateAddress address, long revision, long position, string value) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(position),
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
