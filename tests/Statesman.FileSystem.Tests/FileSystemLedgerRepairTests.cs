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

    [Fact]
    public async Task Dangling_and_position_mismatched_lines_are_dropped_and_the_feed_stops_repeating()
    {
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var moved = new StateAddress("app", "repair/moved", StatePartition.Default);
            await store.ImportAsync(Record(moved, revision: 1, position: 101, "v1"));
            await store.ImportAsync(Record(moved, revision: 1, position: 102, "v2"));

            // Before: one record, two envelopes, and one of them disagrees with itself.
            List<StateChangeEnvelope> before = await DrainAsync(store);
            Assert.Equal(2, before.Count);
            Assert.Contains(before, envelope => envelope.Cursor.Position != envelope.Record.GlobalPosition);

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.False(report.ChangeLogSkipped);
            Assert.Equal(string.Empty, report.ChangeLogSkipReason);
            Assert.Equal(1, report.ChangeLogLinesDropped);

            List<StateChangeEnvelope> after = await DrainAsync(store);
            StateChangeEnvelope single = Assert.Single(after);
            Assert.Equal(102, single.Record.GlobalPosition);
            Assert.Equal(102, single.Cursor.Position);
            Assert.Empty((await store.VerifyAsync()).Findings);

            // The generation moved, which is the cross-process half of the index rebuild.
            Assert.Equal("1\n", await File.ReadAllTextAsync(Path.Combine(directory, "_changes.gen")));

            StateAppendResult result = await store.AppendAsync(
                moved, StateWriteCondition.AtRevision(1), Commit("v3"));
            Assert.True(result.Succeeded);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_restorable_history_file_is_healed_rather_than_having_its_log_line_dropped()
    {
        // The ordering rule, made observable. The head is at revision 2 and revision 2's history file
        // is gone, so line 2 of the log is dangling. Record files are repaired FIRST, so the file comes
        // back and the line stays. Compacting first would drop it and lose that record's feed position
        // for good.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/heal", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string history = HistoryDirectories(directory).Single();
            string newest = Directory.GetFiles(history, "*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(newest);

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.Equal(1, report.HistoryFilesRestored);
            Assert.Equal(0, report.ChangeLogLinesDropped);

            List<StateChangeEnvelope> drained = await DrainAsync(store);
            Assert.Equal([1L, 2L], drained.Select(envelope => envelope.Record.Revision).ToArray());
            Assert.Empty((await store.VerifyAsync()).Findings);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_malformed_line_skips_the_change_log_half_while_record_files_are_still_repaired()
    {
        // CompactChangeLogAsync THROWS on a non-final malformed line rather than skipping it, and
        // dropping the line would lose a feed position silently. So the log half is not attempted --
        // and the record-file half still runs, because an operator with a corrupt log still wants
        // their headless stream back in the catalog.
        //
        // A second, otherwise-healthy stream gives the log a genuinely droppable, well-formed line --
        // the same DanglingChangeLogLine shape the verify tests use -- placed AFTER the malformed line,
        // so bypassing the malformed guard (the break-the-mechanism lever) reaches compaction and
        // compaction has something to throw on. Without this second stream, the log holds only the
        // malformed line and one well-formed, resolvable line, and bypassing the guard would compact
        // cleanly instead of throwing.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/malformed", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string head = HeadFiles(directory).Single();
            string addressHistory = HistoryDirectories(directory).Single();

            var dangling = new StateAddress("app", "repair/malformed-dangling", StatePartition.Default);
            await store.AppendAsync(dangling, StateWriteCondition.Absent, Commit("d1"));
            await store.AppendAsync(dangling, StateWriteCondition.AtRevision(1), Commit("d2"));
            string danglingHistory = HistoryDirectories(directory).Single(path => path != addressHistory);
            string danglingFirst = Directory.GetFiles(danglingHistory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(danglingFirst);

            string log = ChangeLog(directory);
            string[] lines = await File.ReadAllLinesAsync(log);
            await File.WriteAllTextAsync(
                log, lines[0] + "\n" + "garbage\n" + string.Join("\n", lines.Skip(1)) + "\n");
            File.Delete(head);
            byte[] logBefore = await File.ReadAllBytesAsync(log);

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.True(report.ChangeLogSkipped);
            Assert.Contains("line 2", report.ChangeLogSkipReason, StringComparison.Ordinal);
            Assert.Equal(0, report.ChangeLogLinesDropped);
            Assert.Equal(logBefore, await File.ReadAllBytesAsync(log));

            // The record-file half ran anyway.
            Assert.Equal(1, report.HeadsRewritten);
            Assert.True(File.Exists(head));

            // The malformed line is still there and still reported, which is the honest answer: it may
            // name a record, so only an operator can decide what to do with it.
            Assert.Contains(
                FileSystemLedgerFindingKind.MalformedChangeLogLine,
                report.Unrepaired.Select(f => f.Kind));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_dry_run_can_over_count_the_lines_a_real_repair_drops()
    {
        // Not a defect, and worth pinning so nobody "fixes" it into a lie. A dry run does not restore
        // the history file, so the compaction it asks about still sees the line as dangling; the real
        // repair restores the file first and keeps the line. The dry run therefore answers "up to N",
        // which is the safe direction for a number an operator reads before deciding.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "repair/overcount", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("v2"));
            string history = HistoryDirectories(directory).Single();
            File.Delete(Directory.GetFiles(history, "*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .First());

            FileSystemLedgerRepairReport dry = await store.RepairAsync();
            FileSystemLedgerRepairReport real = await store.RepairAsync(dryRun: false);

            Assert.Equal(1, dry.ChangeLogLinesDropped);
            Assert.Equal(0, real.ChangeLogLinesDropped);
            Assert.Equal(1, real.HistoryFilesRestored);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_dry_run_does_not_throw_when_an_unreadable_files_own_line_and_a_droppable_line_coexist()
    {
        // C1 (final review, 2026-09-14): before the fix, CompactChangeLogUnsafeAsync's per-line lookup
        // called ReadFileAsync on every line's history file unconditionally, including one a dry run has
        // not yet quarantined -- so the documented report-only overload threw a raw JsonException on
        // exactly the ordinary damage this phase exists to report (docs/operations/recovery.md's own
        // step 2). The second stream's genuinely dangling line is load-bearing: with no droppable line
        // anywhere, RepairAsync never reaches CompactChangeLogAsync at all, and the throw does not occur.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            // Two revisions, and the OLDER (non-newest) history file is the one corrupted: the head
            // still points at revision 2, whose own history file is untouched, so quarantining revision
            // 1's file does not also orphan the head into a MissingHistoryFile finding.
            var unreadable = new StateAddress("app", "repair/c1-unreadable", StatePartition.Default);
            await store.AppendAsync(unreadable, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(unreadable, StateWriteCondition.AtRevision(1), Commit("v2"));
            string unreadableHistory = HistoryDirectories(directory).Single();
            string unreadableFile = Directory.GetFiles(unreadableHistory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            await File.WriteAllTextAsync(unreadableFile, "this is not json");

            var dangling = new StateAddress("app", "repair/c1-dangling", StatePartition.Default);
            await store.AppendAsync(dangling, StateWriteCondition.Absent, Commit("d1"));
            await store.AppendAsync(dangling, StateWriteCondition.AtRevision(1), Commit("d2"));
            string danglingHistory = HistoryDirectories(directory).Single(path => path != unreadableHistory);
            string danglingFirst = Directory.GetFiles(danglingHistory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(danglingFirst);

            FileSystemLedgerVerificationReport verification = await store.VerifyAsync();
            Assert.Equal(
                [FileSystemLedgerFindingKind.UnreadableRecordFile, FileSystemLedgerFindingKind.DanglingChangeLogLine],
                verification.Findings.Select(f => f.Kind).ToArray());

            FileSystemLedgerRepairReport report = await store.RepairAsync();

            Assert.True(report.DryRun);
            Assert.Equal(1, report.RecordFilesQuarantined);
            // The unreadable file's own line is not provably orphaned -- it exists -- so a dry run,
            // which never quarantines it, keeps that line. Only the second stream's genuinely dangling
            // line is counted as dropped.
            Assert.Equal(1, report.ChangeLogLinesDropped);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Repairing_quarantines_an_unreadable_file_first_then_drops_its_now_absent_line()
    {
        // C1, apply path. Real repair renames the corrupt file out of the read path BEFORE compaction
        // runs, so by the time compaction reaches that file's own line, the file is genuinely gone --
        // the ordinary dangling case -- and the line is dropped along with the other stream's original
        // dangling line, giving dropped=2 against dry run's dropped=1 above.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            var unreadable = new StateAddress("app", "repair/c1-unreadable-real", StatePartition.Default);
            await store.AppendAsync(unreadable, StateWriteCondition.Absent, Commit("v1"));
            await store.AppendAsync(unreadable, StateWriteCondition.AtRevision(1), Commit("v2"));
            string unreadableHistory = HistoryDirectories(directory).Single();
            string unreadableFile = Directory.GetFiles(unreadableHistory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            await File.WriteAllTextAsync(unreadableFile, "this is not json");

            var dangling = new StateAddress("app", "repair/c1-dangling-real", StatePartition.Default);
            await store.AppendAsync(dangling, StateWriteCondition.Absent, Commit("d1"));
            await store.AppendAsync(dangling, StateWriteCondition.AtRevision(1), Commit("d2"));
            string danglingHistory = HistoryDirectories(directory).Single(path => path != unreadableHistory);
            string danglingFirst = Directory.GetFiles(danglingHistory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .First();
            File.Delete(danglingFirst);

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.False(report.DryRun);
            Assert.Equal(1, report.RecordFilesQuarantined);
            Assert.True(File.Exists(unreadableFile + ".corrupt"));
            Assert.Equal(2, report.ChangeLogLinesDropped);
            Assert.Empty((await store.VerifyAsync()).Findings);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Unrepaired_does_not_list_a_change_log_line_the_same_pass_dropped()
    {
        // I1 (final review, 2026-09-14): Unrepaired is accumulated in the record-file loop, which runs
        // BEFORE the change-log half compacts. Before this fix, a Dangling/Mismatch finding therefore
        // landed in Unrepaired and was then dropped by the compaction a few lines later, in the very
        // same pass -- so an operator reading the report was told a line was "left exactly alone" when
        // it was already gone from _changes.log. This is the case the pre-flight scan parked for the
        // final review ("does Unrepaired get computed after compaction?") -- the answer was no.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var moved = new StateAddress("app", "repair/i1-unrepaired", StatePartition.Default);
            await store.ImportAsync(Record(moved, revision: 1, position: 101, "v1"));
            await store.ImportAsync(Record(moved, revision: 1, position: 102, "v2"));

            FileSystemLedgerRepairReport report = await store.RepairAsync(dryRun: false);

            Assert.Equal(1, report.ChangeLogLinesDropped);
            Assert.DoesNotContain(
                FileSystemLedgerFindingKind.ChangeLogPositionMismatch,
                report.Unrepaired.Select(f => f.Kind));
            Assert.Empty(report.Unrepaired);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task A_dry_run_also_excludes_from_Unrepaired_a_line_it_only_predicts_it_would_drop()
    {
        // I1's dry-run half: ChangeLogLinesDropped already answers "would be dropped" on a dry run, so
        // Unrepaired agrees with that number rather than listing the same line as both "would be
        // dropped" and "left exactly alone" at once.
        string directory = TempDirectory();
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "files", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var moved = new StateAddress("app", "repair/i1-dryrun", StatePartition.Default);
            await store.ImportAsync(Record(moved, revision: 1, position: 201, "v1"));
            await store.ImportAsync(Record(moved, revision: 1, position: 202, "v2"));

            FileSystemLedgerRepairReport report = await store.RepairAsync();

            Assert.True(report.DryRun);
            Assert.Equal(1, report.ChangeLogLinesDropped);
            Assert.Empty(report.Unrepaired);
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
