using System.Globalization;
using System.Text.Json;

namespace Statesman;

/// <summary>What one verify pass found wrong about a stored ledger.</summary>
public enum FileSystemLedgerFindingKind
{
    /// <summary>
    /// The final line of <c>_changes.log</c> does not parse — either because it has no terminating
    /// newline, which is what a crash mid-append leaves, or because it is terminated but malformed.
    /// Every read path tolerates it and compaction copies it through, so the kind names the tail
    /// rather than the mechanism.
    /// </summary>
    TornChangeLogTail = 0,

    /// <summary>
    /// A line of <c>_changes.log</c> that is not the last one and does not parse. Only the final line
    /// can be a partially-written append, so this means the log is corrupt: a read throws
    /// <see cref="InvalidDataException"/> naming the same 1-based line number this finding carries.
    /// </summary>
    MalformedChangeLogLine = 1,

    /// <summary>
    /// A change-log line whose history file no longer exists, which is what pruning leaves behind. A
    /// read skips it silently.
    /// </summary>
    DanglingChangeLogLine = 2,

    /// <summary>
    /// A change-log line whose history file exists but carries a different global position, which is
    /// what an import that moved a revision leaves behind. The record is yielded twice.
    /// </summary>
    ChangeLogPositionMismatch = 3,

    /// <summary>
    /// A stream directory holding history files with no <c>head.json</c>. Reads by address still
    /// recover from history, but the address is skipped by the partition catalog and therefore omitted
    /// from every export.
    /// </summary>
    MissingHead = 4,

    /// <summary>
    /// A <c>head.json</c> whose revision has no history file. Pruning cannot produce this, because it
    /// always retains the latest revision.
    /// </summary>
    MissingHistoryFile = 5,

    /// <summary>
    /// A record file that exists and whose whole contents are not a deserializable record. One of
    /// these makes an unbounded change-feed read throw for the entire store.
    /// </summary>
    UnreadableRecordFile = 6,

    /// <summary>
    /// A <c>.tmp</c> file left by a crash between a temporary write and its rename. Invisible to every
    /// read path, and never removed for you: a temporary file is indistinguishable from a live
    /// in-flight write.
    /// </summary>
    OrphanedTemporaryFile = 7,

    /// <summary>
    /// A stream directory whose name is not the hash of the address its own records carry. Only a
    /// directory walk finds it; no read by address ever will.
    /// </summary>
    MisplacedStreamDirectory = 8,
}

/// <summary>One thing a verify pass found, and where.</summary>
public sealed record FileSystemLedgerFinding
{
    /// <summary>What kind of damage this is.</summary>
    public required FileSystemLedgerFindingKind Kind { get; init; }

    /// <summary>The absolute path the finding concerns — the file that is wrong, or the one that is missing.</summary>
    public required string Path { get; init; }

    /// <summary>The address this finding belongs to, when the pass could determine one.</summary>
    public StateAddress? Address { get; init; }

    /// <summary>The 1-based change-log line number, for the four change-log kinds.</summary>
    public long? ChangeLogLine { get; init; }

    /// <summary>The revision this finding concerns, when it concerns one.</summary>
    public long? Revision { get; init; }

    /// <summary>A sentence an operator can act on, naming the values that disagree.</summary>
    public required string Detail { get; init; }
}

/// <summary>What one verify pass scanned, and what it found.</summary>
/// <remarks>
/// Verify writes nothing at all. It takes the change-feed gate for the log scan, so it is ordered
/// against this process's own writer, and takes no per-address gate — a store being written while it
/// is verified can produce a finding that describes a normal mid-append state rather than damage, so
/// quiesce the writer before acting on a report.
/// </remarks>
public sealed record FileSystemLedgerVerificationReport
{
    /// <summary>Stream directories walked.</summary>
    public long StreamsScanned { get; init; }

    /// <summary>Head and history files opened.</summary>
    public long RecordFilesScanned { get; init; }

    /// <summary>Complete change-log lines read. An unterminated final fragment is not counted, because it is not a line yet.</summary>
    public long ChangeLogLinesScanned { get; init; }

    /// <summary>Everything wrong, in scan order: stream directories first, then the change log by ascending line number.</summary>
    public IReadOnlyList<FileSystemLedgerFinding> Findings { get; init; } = [];

    /// <summary>How many findings there are.</summary>
    public int FindingCount => Findings.Count;
}

/// <summary>What one repair pass did, or would do on a dry run.</summary>
/// <remarks>
/// Repair never destroys data a verify pass could not prove orphaned. It writes two kinds of record
/// file back from data already in the same stream directory, takes an unreadable record file out of
/// the read path by renaming it rather than deleting it, and drops only the change-log lines that
/// provably dereference to nothing. Everything else is reported in <see cref="Unrepaired"/> and left
/// exactly alone.
/// </remarks>
public sealed record FileSystemLedgerRepairReport
{
    /// <summary>True when this was a dry run and nothing was written.</summary>
    public bool DryRun { get; init; }

    /// <summary>Head files written back from the newest history file, or that would be.</summary>
    public long HeadsRewritten { get; init; }

    /// <summary>History files written back from the head, or that would be.</summary>
    public long HistoryFilesRestored { get; init; }

    /// <summary>Unreadable record files renamed out of the read path, or that would be.</summary>
    public long RecordFilesQuarantined { get; init; }

    /// <summary>Change-log lines dropped, or that would be dropped, by the compaction this pass runs.</summary>
    public long ChangeLogLinesDropped { get; init; }

    /// <summary>True when the change-log half was not attempted at all.</summary>
    public bool ChangeLogSkipped { get; init; }

    /// <summary>Why the change-log half was skipped, or empty when it was not.</summary>
    public string ChangeLogSkipReason { get; init; } = string.Empty;

    /// <summary>Every finding this pass deliberately did not act on, in the verification's own order.</summary>
    public IReadOnlyList<FileSystemLedgerFinding> Unrepaired { get; init; } = [];

    /// <summary>The verification this repair acted on. Re-run <c>VerifyAsync</c> afterwards: one repair can expose the next finding.</summary>
    public FileSystemLedgerVerificationReport Verification { get; init; } = new();
}

public sealed partial class FileSystemStateLedgerStore
{
    /// <summary>
    /// Reports everything wrong with this store's directory without changing a byte of it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was scanned, and every finding in scan order.</returns>
    /// <remarks>
    /// Pair this with <c>RepairAsync</c>: verify to see, a dry-run repair to see what would be done,
    /// then a real repair. Run it with the writer stopped — this provider is single-writer by design,
    /// and a finding taken from a live store can describe a normal mid-append state.
    /// </remarks>
    public async ValueTask<FileSystemLedgerVerificationReport> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        var findings = new List<FileSystemLedgerFinding>();

        // Streams first, then the log, because that is the order RepairAsync acts in: a dangling
        // change-log line whose history file this same pass can restore must be healed rather than
        // dropped. Pre-Phase-18 addendum decision 88.
        //
        // No per-address gate. Determining a stream's address means reading one of its files, so a
        // gate could only be taken after the read it was meant to protect; and this is a diagnostic
        // over a store an operator is expected to have quiesced. The share modes are the provider's
        // own, so a live writer is never blocked -- it can only make a finding describe a normal
        // mid-append state, which the report's own remarks say.
        (long streams, long recordFiles) = await VerifyStreamsAsync(findings, cancellationToken)
            .ConfigureAwait(false);

        long changeLogLines;
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changeLogLines = await VerifyChangeLogUnsafeAsync(findings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changeFeedGate.Release();
        }

        return new FileSystemLedgerVerificationReport
        {
            StreamsScanned = streams,
            RecordFilesScanned = recordFiles,
            ChangeLogLinesScanned = changeLogLines,
            Findings = findings,
        };
    }

    // The root holds two-hex-character prefix directories plus _changes.log, _changes.gen and any
    // root-level temporary. EnumerateDirectories therefore yields exactly the prefixes, and the
    // ordinal sort makes the finding order deterministic across runs and platforms.
    private async ValueTask<(long Streams, long RecordFiles)> VerifyStreamsAsync(
        List<FileSystemLedgerFinding> findings,
        CancellationToken cancellationToken)
    {
        long streams = 0;
        long recordFiles = 0;

        foreach (string prefix in Directory.EnumerateDirectories(_rootDirectory)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (string stream in Directory.EnumerateDirectories(prefix)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                streams++;
                recordFiles += await VerifyStreamAsync(stream, findings, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (string temporary in Directory.EnumerateFiles(_rootDirectory, "*.tmp")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            findings.Add(OrphanedTemporary(temporary));
        }

        return (streams, recordFiles);
    }

    private async ValueTask<long> VerifyStreamAsync(
        string streamDirectory,
        List<FileSystemLedgerFinding> findings,
        CancellationToken cancellationToken)
    {
        string headFile = System.IO.Path.Combine(streamDirectory, "head.json");
        string historyDirectory = System.IO.Path.Combine(streamDirectory, "history");
        long recordFiles = 0;

        FileRecord? head = null;
        if (File.Exists(headFile))
        {
            recordFiles++;
            (bool readable, head) = await ProbeRecordFileAsync(headFile, cancellationToken)
                .ConfigureAwait(false);
            if (!readable)
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = FileSystemLedgerFindingKind.UnreadableRecordFile,
                    Path = headFile,
                    Detail = "The head file exists but its contents are not a record. Reads by address fall back to the newest history file, but an export and an unbounded feed read both fail on it.",
                });
            }
        }

        var history = new List<FileRecord>();
        if (Directory.Exists(historyDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(historyDirectory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                recordFiles++;
                (bool readable, FileRecord? record) = await ProbeRecordFileAsync(file, cancellationToken)
                    .ConfigureAwait(false);
                if (!readable)
                {
                    findings.Add(new FileSystemLedgerFinding
                    {
                        Kind = FileSystemLedgerFindingKind.UnreadableRecordFile,
                        Path = file,
                        Detail = "The history file exists but its contents are not a record. One of these makes an unbounded change-feed read throw for the entire store.",
                    });
                    continue;
                }

                if (record is not null)
                {
                    history.Add(record);
                }
            }

            foreach (string temporary in Directory.EnumerateFiles(historyDirectory, "*.tmp")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                findings.Add(OrphanedTemporary(temporary));
            }
        }

        foreach (string temporary in Directory.EnumerateFiles(streamDirectory, "*.tmp")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            findings.Add(OrphanedTemporary(temporary));
        }

        // The directory name is a hash, so the address can only come from a record inside it. With
        // neither the head nor any history file readable there is nothing to identify the stream by,
        // and the UnreadableRecordFile findings above already say exactly why.
        FileRecord? newest = history.Count == 0
            ? null
            : history.OrderByDescending(record => record.Revision).First();
        FileRecord? identity = head ?? newest;
        if (identity is null)
        {
            return recordFiles;
        }

        var address = new StateAddress(
            identity.Root, new StatePath(identity.Path), new StatePartition(identity.Partition));

        if (!string.Equals(StreamDirectory(address), streamDirectory, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new FileSystemLedgerFinding
            {
                Kind = FileSystemLedgerFindingKind.MisplacedStreamDirectory,
                Path = streamDirectory,
                Address = address,
                Detail = $"This directory holds records for '{address.Canonical}', whose stream directory is '{StreamDirectory(address)}'. No read by address will find these records.",
            });
        }

        if (head is null && history.Count > 0)
        {
            findings.Add(new FileSystemLedgerFinding
            {
                Kind = FileSystemLedgerFindingKind.MissingHead,
                Path = headFile,
                Address = address,
                Revision = newest!.Revision,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"This stream holds history up to revision {newest.Revision} with no head file. Reads by address still recover from history, but the partition catalog skips the address, so every export omits the whole stream."),
            });
        }

        // File.Exists rather than a search of the loaded records: a history file that EXISTS but is
        // unreadable is already reported above, and must not also be reported as missing -- repair
        // would then write over a damaged file instead of quarantining it.
        if (head is not null)
        {
            string expected = HistoryFile(address, head.Revision);
            if (!File.Exists(expected))
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = FileSystemLedgerFindingKind.MissingHistoryFile,
                    Path = expected,
                    Address = address,
                    Revision = head.Revision,
                    Detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"The head is at revision {head.Revision}, whose history file is absent. Pruning cannot produce this: every retention filter exempts the latest revision."),
                });
            }
        }

        return recordFiles;
    }

    private static FileSystemLedgerFinding OrphanedTemporary(string path) => new()
    {
        Kind = FileSystemLedgerFindingKind.OrphanedTemporaryFile,
        Path = path,
        Detail = "A temporary file left by a crash between a write and its rename. It is invisible to every read path and is never removed for you: with the writer running, a temporary file is indistinguishable from a live in-flight write.",
    };

    // The caller must already hold _changeFeedGate. CompactChangeLogUnsafeAsync's scan, reporting
    // instead of rewriting: same line splitting, same one-deserialization-per-distinct-history-file
    // rule, same reading of what a null or differing stored position means.
    private async ValueTask<long> VerifyChangeLogUnsafeAsync(
        List<FileSystemLedgerFinding> findings,
        CancellationToken cancellationToken)
    {
        byte[] existing;
        FileStream source;
        try
        {
            source = new FileStream(
                ChangeFeedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // No log means nothing to verify, not an error -- the same reading every other method in
            // this provider gives a missing change log.
            return 0;
        }

        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            existing = buffer.ToArray();
        }

        // Each distinct history file is deserialized ONCE, not once per line, for the same reason
        // compaction does it: a long log is long because one address was written many times. The
        // Readable half of the cached probe is what separates "the file is gone" (DanglingChangeLogLine
        // -- repair may drop the line) from "the file is there but damaged" (already reported once by
        // the stream walk as UnreadableRecordFile; reporting it again here as dangling would tell
        // repair the line is safe to drop, when the record it names still exists, just unreadable).
        var positions = new Dictionary<string, (bool Readable, long? Position)>(StringComparer.OrdinalIgnoreCase);
        long lines = 0;
        int start = 0;
        while (start < existing.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int newline = Array.IndexOf(existing, (byte)'\n', start);
            if (newline < 0)
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = FileSystemLedgerFindingKind.TornChangeLogTail,
                    Path = ChangeFeedFile,
                    Detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"The change log ends in {existing.Length - start} byte(s) with no terminating newline, which is what a crash mid-append leaves. The next append starts a fresh line rather than merging into it."),
                });
                break;
            }

            lines++;
            string line = DecodeChangeFeedLine(existing, start, newline - start);
            bool isFinal = newline + 1 == existing.Length;
            start = newline + 1;
            if (line.Length == 0)
            {
                // An empty line dereferences nothing and is skipped on read anyway.
                continue;
            }

            if (!TryParseChangeFeedLine(line, out long position, out StateAddress address, out long revision))
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = isFinal
                        ? FileSystemLedgerFindingKind.TornChangeLogTail
                        : FileSystemLedgerFindingKind.MalformedChangeLogLine,
                    Path = ChangeFeedFile,
                    ChangeLogLine = lines,
                    Detail = isFinal
                        ? $"The change log's final line does not parse: '{line}'. Every read path tolerates an unparseable final line and compaction copies it through."
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"The change log has a malformed entry at line {lines}: '{line}'. Only the final line can be a partially-written append, so a malformed line before the end means the log is corrupt and every read of it throws."),
                });
                continue;
            }

            string historyFile = HistoryFile(address, revision);
            if (!positions.TryGetValue(historyFile, out (bool Readable, long? Position) probe))
            {
                (bool readable, FileRecord? record) = await ProbeRecordFileAsync(historyFile, cancellationToken)
                    .ConfigureAwait(false);
                probe = (readable, record?.GlobalPosition);
                positions[historyFile] = probe;
            }

            if (!probe.Readable)
            {
                // The stream walk already reported this file as UnreadableRecordFile; it exists, so
                // this line is not dangling and its position cannot be compared.
                continue;
            }

            if (probe.Position is null)
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = FileSystemLedgerFindingKind.DanglingChangeLogLine,
                    Path = historyFile,
                    Address = address,
                    ChangeLogLine = lines,
                    Revision = revision,
                    Detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Change-log line {lines} names revision {revision} at position {position}, whose history file is gone. A read skips it silently."),
                });
            }
            else if (probe.Position != position)
            {
                findings.Add(new FileSystemLedgerFinding
                {
                    Kind = FileSystemLedgerFindingKind.ChangeLogPositionMismatch,
                    Path = historyFile,
                    Address = address,
                    ChangeLogLine = lines,
                    Revision = revision,
                    Detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Change-log line {lines} carries position {position} for revision {revision}, whose history file carries {probe.Position}. The record is yielded twice."),
                });
            }
        }

        return lines;
    }

    // ReadFileAsync answers null both for "no such file" and -- because DeserializeAsync does -- for a
    // file whose whole contents are the JSON literal null, and it THROWS for contents that are not
    // JSON at all. Verify must do neither: it must never throw, and it must never let a file that
    // exists but carries nothing look like an absent one, because repair reads "absent" as a line it
    // may drop.
    private async ValueTask<(bool Readable, FileRecord? Record)> ProbeRecordFileAsync(
        string file,
        CancellationToken cancellationToken)
    {
        FileRecord? record;
        try
        {
            record = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return (false, null);
        }

        return record is null && File.Exists(file) ? (false, null) : (true, record);
    }

    /// <summary>
    /// Reports what a repair would do, without doing any of it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What a repair would change.</returns>
    /// <remarks>
    /// This overload is a <b>dry run</b>, deliberately unlike
    /// <see cref="CompactChangeLogAsync(System.Threading.CancellationToken)"/>, whose parameterless
    /// overload applies. Compaction only removes lines it has proved dereference to nothing, so a
    /// misread call costs a rewrite; repair renames and writes files, so the zero-argument call is the
    /// safe one. Pass <c>dryRun: false</c> to apply.
    /// </remarks>
    public ValueTask<FileSystemLedgerRepairReport> RepairAsync(
        CancellationToken cancellationToken = default) =>
        RepairAsync(dryRun: true, cancellationToken);

    /// <summary>
    /// Repairs what can be repaired without destroying anything, or reports what it would repair.
    /// </summary>
    /// <param name="dryRun">When true, nothing is written and the result reports what would change.</param>
    /// <param name="cancellationToken">Cancels the scan. A cancelled repair leaves whatever it had already written.</param>
    /// <returns>What was changed, what was not, and the verification it acted on.</returns>
    /// <remarks>
    /// Run it with the writer stopped. Record files are repaired before the change log is compacted,
    /// so a change-log line whose history file this pass restores is healed rather than dropped; and
    /// one repair can expose the next finding, so re-run <see cref="VerifyAsync"/> afterwards.
    /// On a dry run the change-log count can exceed what a real repair drops, because a dry run does
    /// not restore the history files that would make some of those lines dereference again.
    /// </remarks>
    public async ValueTask<FileSystemLedgerRepairReport> RepairAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        FileSystemLedgerVerificationReport verification =
            await VerifyAsync(cancellationToken).ConfigureAwait(false);

        long heads = 0;
        long histories = 0;
        long quarantined = 0;
        var unrepaired = new List<FileSystemLedgerFinding>();

        // Quarantine before restore, within the record-file half: a head that is unreadable becomes a
        // MISSING head on the next pass, and repairing both in one pass would mean writing over the
        // damaged file rather than keeping it.
        foreach (FileSystemLedgerFinding finding in verification.Findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (finding.Kind)
            {
                case FileSystemLedgerFindingKind.UnreadableRecordFile:
                    quarantined++;
                    if (!dryRun)
                    {
                        File.Move(finding.Path, QuarantineName(finding.Path));
                    }

                    break;

                case FileSystemLedgerFindingKind.MissingHead when finding.Address is StateAddress head:
                    heads++;
                    if (!dryRun)
                    {
                        await RewriteHeadAsync(head, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case FileSystemLedgerFindingKind.MissingHistoryFile when finding.Address is StateAddress missing:
                    histories++;
                    if (!dryRun)
                    {
                        await RestoreHistoryFileAsync(missing, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                default:
                    unrepaired.Add(finding);
                    break;
            }
        }

        // Two rules, each load-bearing. First: this runs AFTER the record-file loop above, so a
        // change-log line whose history file that loop just restored is healed rather than dropped --
        // compaction running first would drop it and lose that record's feed position for good.
        // Second: a malformed line that is not the last one makes CompactChangeLogAsync THROW rather
        // than skip, and dropping it here would lose a feed position silently, which is the exact
        // outcome the read path's corruption exception exists to prevent. So the whole log half is
        // skipped and said to be skipped, while the record-file half above has already run.
        // Pre-Phase-18 addendum decision 88.
        //
        // Routing the only log rewrite through CompactChangeLogAsync is also what makes the index
        // rebuild free: that method bumps _changes.gen BEFORE the rename, which is every other
        // process's invalidation signal, and resets this store's in-process index after it. Nothing
        // else in this provider may rewrite the log. Decision 84.
        long dropped = 0;
        bool skipped = false;
        string skipReason = string.Empty;
        FileSystemLedgerFinding? malformed = verification.Findings
            .FirstOrDefault(finding => finding.Kind == FileSystemLedgerFindingKind.MalformedChangeLogLine);
        bool hasDroppableLine = verification.Findings.Any(finding =>
            finding.Kind is FileSystemLedgerFindingKind.DanglingChangeLogLine
                or FileSystemLedgerFindingKind.ChangeLogPositionMismatch);

        if (malformed is not null)
        {
            skipped = true;
            skipReason = string.Create(
                CultureInfo.InvariantCulture,
                $"The change log has a malformed entry at line {malformed.ChangeLogLine}, which may name a record that exists. Dropping it would lose a feed position silently, so the change log was left untouched. Stop the writer, take a copy, and decide by hand.");
        }
        else if (hasDroppableLine)
        {
            ChangeLogCompactionResult compaction =
                await CompactChangeLogAsync(dryRun, cancellationToken).ConfigureAwait(false);
            dropped = compaction.LinesBefore - compaction.LinesAfter;
        }

        return new FileSystemLedgerRepairReport
        {
            DryRun = dryRun,
            HeadsRewritten = heads,
            HistoryFilesRestored = histories,
            RecordFilesQuarantined = quarantined,
            ChangeLogLinesDropped = dropped,
            ChangeLogSkipped = skipped,
            ChangeLogSkipReason = skipReason,
            Unrepaired = unrepaired,
            Verification = verification,
        };
    }

    // ReadLatestUnsafeAsync already computes exactly the value a head should hold: it prefers a newer
    // history file over a stale head, which is the self-healing rule AppendAsync's head write outside
    // the change-feed gate relies on. Writing it back makes that recovery durable, and it is what
    // takes the address out of the partition catalog's blind spot.
    private async ValueTask RewriteHeadAsync(StateAddress address, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateRecord? latest = await ReadLatestUnsafeAsync(address, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                await AtomicWriteAsync(HeadFile(address), new FileRecord(latest), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // The head IS the record for its own revision, so writing it into the history directory restores
    // the invariant from data already on disk. Safe because pruning provably cannot delete the latest
    // revision's file: every retention filter exempts it, so this never resurrects something retention
    // deliberately removed. Pre-Phase-18 addendum decision 83.
    private async ValueTask RestoreHistoryFileAsync(StateAddress address, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileRecord? head = await ReadFileAsync(HeadFile(address), cancellationToken).ConfigureAwait(false);
            if (head is not null)
            {
                await AtomicWriteAsync(HistoryFile(address, head.Revision), head, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // A suffix rather than a deletion, and a numbered one rather than a clock-derived one, so a second
    // repair of the same file is deterministic and never overwrites the first quarantine.
    private static string QuarantineName(string file)
    {
        string candidate = file + ".corrupt";
        for (int index = 1; File.Exists(candidate); index++)
        {
            candidate = file + ".corrupt." + index.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
