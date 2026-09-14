using System.Text;

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
