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

        // The change-feed gate alone, and only for the log scan. The lock order this provider
        // establishes is Gate(address) -> _changeFeedGate, so taking the second alone inverts nothing.
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
            ChangeLogLinesScanned = changeLogLines,
            Findings = findings,
        };
    }

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
        // compaction does it: a long log is long because one address was written many times.
        var positions = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
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
            if (!positions.TryGetValue(historyFile, out long? stored))
            {
                (_, FileRecord? record) = await ProbeRecordFileAsync(historyFile, cancellationToken)
                    .ConfigureAwait(false);
                stored = record?.GlobalPosition;
                positions[historyFile] = stored;
            }

            if (stored is null)
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
            else if (stored != position)
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
                        $"Change-log line {lines} carries position {position} for revision {revision}, whose history file carries {stored}. The record is yielded twice."),
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
}
