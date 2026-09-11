using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Statesman;

public sealed class FileSystemStateLedgerStoreOptions
{
    public required string RootDirectory { get; set; }

    public bool FlushToDisk { get; set; } = true;
}

/// <summary>What one change-log compaction did, or would do on a dry run.</summary>
/// <remarks>
/// Compaction is a storage and scan-cost operation. A pruned record's change-log line is already
/// skipped on read, so removing it reclaims disk and shortens the whole-log scan
/// <c>ListPartitionsAsync</c> still pays — it does not change which records the feed yields, with
/// one exception: a line left behind by an import that moved a revision to a different
/// <c>GlobalPosition</c> is dropped, and that record stops being yielded twice.
/// </remarks>
public sealed record ChangeLogCompactionResult
{
    /// <summary>True when this was a dry run and nothing was written.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Complete lines the change log held before compaction. An unterminated final fragment is not
    /// counted, because it is not a line yet.
    /// </summary>
    public long LinesBefore { get; init; }

    /// <summary>Complete lines the compacted log holds, or would hold on a dry run.</summary>
    public long LinesAfter { get; init; }

    /// <summary>Bytes the change log held before compaction.</summary>
    public long BytesBefore { get; init; }

    /// <summary>Bytes the compacted log holds, or would hold on a dry run.</summary>
    public long BytesAfter { get; init; }

    /// <summary>Bytes reclaimed, or that would be reclaimed. Never negative: compaction only removes lines.</summary>
    public long BytesReclaimed => BytesBefore - BytesAfter;
}

public sealed class FileSystemStateLedgerStore : IStateLedgerStore, IStateLedgerReplica, IStateChangeFeed, IPartitionCatalog
{
    private readonly string _rootDirectory;
    private readonly bool _flushToDisk;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changeFeedGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
    private long _globalPosition;

    // How many index entries one chunk of the yield loop copies out from under _changeFeedGate.
    // Bounded so the gate is held for a chunk rather than for a whole page of history-file reads;
    // 256 is large enough that a default 100-record page is one chunk and small enough that a
    // pathological unbounded read does not copy the whole index in one hold.
    private const int ChangeFeedCopyChunk = 256;

    // The in-process change-log index. Phase 10's paging made the outbox's page loop pay one whole
    // change-log scan per page rather than per cycle, quadratic in backlog size; this makes each
    // ReadAsync parse only the bytes appended since the last one.
    //
    // Every field below is read and written ONLY under _changeFeedGate, which every reader and this
    // process's writer already hold. The index is per store instance and per process: nothing is
    // persisted, and a second process pays its own first parse. That is the same single-writer
    // premise AppendChangeFeedEntryUnsafeAsync's comment already states -- neither strengthened nor
    // weakened by this.
    //
    // Kept sorted by position, NOT in file order. ImportAsync appends a line carrying the imported
    // record's own GlobalPosition, which can be lower than positions already in the log, and
    // restoring into a non-empty target is a documented, supported scenario -- so the index stores
    // entries and inserts each at its sorted place rather than assuming file order is position order.
    private readonly List<ChangeFeedEntry> _indexEntries = [];

    // Interned addresses, so N lines over K distinct addresses hold K StateAddress values rather
    // than N. A StateAddress is three strings; without this a 200,000-line log would retain 200,000
    // copies of a thousand of them.
    private readonly Dictionary<StateAddress, StateAddress> _indexAddresses = [];

    // The offset one past the last complete, well-formed line consumed into _indexEntries. An
    // unterminated final fragment is never consumed, so this always sits on a line boundary.
    private long _indexScannedBytes;

    // Lines consumed, so CorruptChangeFeedLine still names the right 1-based line when the
    // corruption is in a suffix this read is the first to see. Counts every line StreamReader would
    // have produced, empty ones included, because the numbering the shipped message uses does.
    private int _indexLineCount;

    // The raw bytes of the last consumed line, terminator included. Re-read and compared on every
    // read: the file-length check below catches a truncation or a compaction, and this catches a
    // rewrite that kept the length -- which a length check alone cannot see, and which would
    // otherwise leave the index serving lines that no longer exist. It is not belt and braces; it
    // is the second half of the invalidation rule.
    private byte[] _indexTail = [];

    // The compaction generation this index was built from. Written ONLY in
    // RefreshChangeFeedIndexUnsafeAsync, always to the value just read from _changes.gen.
    // ResetChangeFeedIndexUnsafe deliberately does not touch it: an index discarded for a shrink or
    // a tail mismatch says nothing about which file generation this store last saw, and zeroing it
    // there would force a second, pointless discard on the next read.
    private long _indexGeneration;

    public FileSystemStateLedgerStore(
        string name,
        FileSystemStateLedgerStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        Name = name;
        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        _flushToDisk = options.FlushToDisk;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_rootDirectory);
    }

    public string Name { get; }

    public async ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        SemaphoreSlim gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLatestUnsafeAsync(address, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        address.Validate();
        options.Validate();
        SemaphoreSlim gate = Gate(address);
        FileRecord[] records;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string historyDirectory = HistoryDirectory(address);
            if (!Directory.Exists(historyDirectory))
            {
                yield break;
            }

            var loaded = new List<FileRecord>();
            foreach (string file in Directory.EnumerateFiles(historyDirectory, "*.json"))
            {
                FileRecord? record = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    loaded.Add(record);
                }
            }

            IEnumerable<FileRecord> query = loaded;
            if (options.BeforeRevision is long before)
            {
                query = query.Where(record => record.Revision < before);
            }

            if (options.Since is DateTimeOffset since)
            {
                query = query.Where(record => record.OccurredAt >= since);
            }

            query = options.NewestFirst
                ? query.OrderByDescending(record => record.Revision)
                : query.OrderBy(record => record.Revision);
            if (options.Take is int take)
            {
                query = query.Take(take);
            }

            records = query.ToArray();
        }
        finally
        {
            gate.Release();
        }

        foreach (FileRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record.ToStateRecord();
        }
    }

    public async ValueTask<StateAppendResult> AppendAsync(
        StateAddress address,
        StateWriteCondition condition,
        StateCommit commit,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        condition.Validate();
        commit.Validate();
        SemaphoreSlim gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateRecord? current = await ReadLatestUnsafeAsync(address, cancellationToken).ConfigureAwait(false);
            if (!Matches(current, condition))
            {
                return StateAppendResult.Conflict(current);
            }

            StateRecord record;
            FileRecord model;

            // Commit-time position allocation. Allocating the position, durably writing the
            // history file, and appending the change-log line happen as one step under the global
            // change-feed gate, so a position can never appear on the feed while a lower one is
            // still unpublished -- including for a reader in a different process, which is the
            // only thing this log file exists for.
            //
            // Only the history file belongs inside. ReadAsync re-reads each record from
            // HistoryFile(address, revision) and never from the head file, and
            // ReadLatestUnsafeAsync already prefers a newer history file over a stale head, so a
            // head write lost to a crash self-heals on the next read. Keeping the head write
            // outside halves the fsync cost this gate serializes.
            await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                record = new StateRecord
                {
                    Address = address,
                    Revision = (current?.Revision ?? 0) + 1,
                    GlobalPosition = NextGlobalPosition(),
                    OccurredAt = _timeProvider.GetUtcNow(),
                    Operation = commit.Operation,
                    Status = commit.Status,
                    ValueType = commit.ValueType,
                    SchemaVersion = commit.SchemaVersion,
                    Payload = commit.Payload?.ToArray(),
                    FreshUntil = commit.FreshUntil,
                    ServeUntil = commit.ServeUntil,
                    Source = commit.Source,
                    CorrelationId = commit.CorrelationId,
                    CausationId = commit.CausationId,
                    Metadata = new Dictionary<string, string>(commit.Metadata, StringComparer.OrdinalIgnoreCase),
                    Error = commit.Error,
                };
                model = new FileRecord(record);
                await AtomicWriteAsync(HistoryFile(address, record.Revision), model, cancellationToken).ConfigureAwait(false);

                // The history write above has already durably committed, so this bookkeeping
                // append must not be cancellable by the caller's token -- cancelling it here
                // would surface a spurious OperationCanceledException for an append that
                // actually succeeded, and a caller retry would then see a false Conflict.
                await AppendChangeFeedEntryUnsafeAsync(record, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _changeFeedGate.Release();
            }

            await AtomicWriteAsync(HeadFile(address), model, CancellationToken.None).ConfigureAwait(false);
            return StateAppendResult.Appended(record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        SemaphoreSlim gate = Gate(record.Address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateRecord? current = await ReadLatestUnsafeAsync(record.Address, cancellationToken).ConfigureAwait(false);
            string historyFile = HistoryFile(record.Address, record.Revision);
            FileRecord? existingRevision = await ReadFileAsync(historyFile, cancellationToken).ConfigureAwait(false);
            bool isNewPosition = existingRevision is null || existingRevision.GlobalPosition != record.GlobalPosition;
            var model = new FileRecord(record);

            // The same critical section AppendAsync uses, for the same reason: the history write,
            // the high-water advance, and the change-log append must be one step, so no concurrent
            // append can allocate at or below an import that has not been published yet.
            await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Replica import is exact, not append-if-absent. Replacing a divergent
                // revision allows the cold authority to repair a corrupt hot replica.
                await AtomicWriteAsync(historyFile, model, cancellationToken).ConfigureAwait(false);
                AdvanceGlobalPosition(record.GlobalPosition);
                if (isNewPosition)
                {
                    // The primary write above has already durably committed, so this
                    // bookkeeping append must not be cancellable by the caller's token -- see
                    // the matching comment in AppendAsync for why.
                    //
                    // The residue this leaves, documented rather than fixed in Phase 11: the history
                    // file is REPLACED in place while the change log only ever grows, so re-importing
                    // an existing revision at a different position leaves the old log line pointing
                    // at the rewritten file and that record yields at two positions. Same
                    // colliding-lineage restore as the other two providers; see
                    // docs/providers/index.md.
                    await AppendChangeFeedEntryUnsafeAsync(record, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _changeFeedGate.Release();
            }

            if (current is null || current.Revision <= record.Revision)
            {
                await AtomicWriteAsync(HeadFile(record.Address), model, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        policy.Validate();
        SemaphoreSlim gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = HistoryDirectory(address);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var records = new List<(string File, FileRecord Record)>();
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                FileRecord? record = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    records.Add((file, record));
                }
            }

            if (records.Count <= 1)
            {
                return;
            }

            long latest = records.Max(value => value.Record.Revision);
            IEnumerable<(string File, FileRecord Record)> retained = records;
            if (policy.MaxAge is TimeSpan age)
            {
                DateTimeOffset cutoff = _timeProvider.GetUtcNow() - age;
                retained = retained.Where(value => value.Record.OccurredAt >= cutoff || value.Record.Revision == latest);
            }

            if (!policy.KeepTombstones)
            {
                retained = retained.Where(value => value.Record.Operation != StateOperation.Cleared || value.Record.Revision == latest);
            }

            List<(string File, FileRecord Record)> keep = retained.OrderBy(value => value.Record.Revision).ToList();
            if (policy.MaxRevisions is int maxRevisions && keep.Count > maxRevisions)
            {
                keep = keep.Skip(keep.Count - maxRevisions).ToList();
            }

            if (policy.MaxBytes is long maxBytes)
            {
                long bytes = 0;
                var sized = new List<(string File, FileRecord Record)>();
                foreach ((string file, FileRecord record) in keep.OrderByDescending(value => value.Record.Revision))
                {
                    long length = record.Payload?.LongLength ?? 0;
                    if (sized.Count > 0 && bytes + length > maxBytes)
                    {
                        continue;
                    }

                    sized.Add((file, record));
                    bytes += length;
                }

                keep = sized.OrderBy(value => value.Record.Revision).ToList();
            }

            var keepFiles = keep.Select(value => value.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach ((string file, FileRecord _) in records)
            {
                if (!keepFiles.Contains(file))
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Rewrites <c>_changes.log</c> without the lines that no longer dereference to a stored record.
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan. A cancelled compaction leaves the log untouched.</param>
    /// <remarks>
    /// <para>
    /// Two kinds of line are dropped: one whose history file is gone, which is what
    /// <see cref="PruneAsync"/> leaves behind; and one whose history file exists but carries a
    /// different <c>GlobalPosition</c>, which is what an import that moved a revision leaves behind
    /// and which makes that record yield twice. An unterminated final line is copied through
    /// untouched, and no two byte-identical lines are ever emitted.
    /// </para>
    /// <para>
    /// This must run in the writer's own process. The provider is single-writer by design — the
    /// counter that allocates <c>GlobalPosition</c> lives in memory — and an appender takes two file
    /// opens, so a compactor in another process could rename the log between a writer's tail check
    /// and its append.
    /// </para>
    /// <para>
    /// It is not called for you. <c>StateHandle</c> prunes after every successful append, so an
    /// automatic compaction on that path would make an unrelated address's append wait out a
    /// whole-file rewrite. Call it on a maintenance cadence instead.
    /// </para>
    /// </remarks>
    public ValueTask<ChangeLogCompactionResult> CompactChangeLogAsync(
        CancellationToken cancellationToken = default) =>
        CompactChangeLogAsync(dryRun: false, cancellationToken);

    /// <summary>
    /// Rewrites <c>_changes.log</c>, or reports what a rewrite would do without performing it.
    /// </summary>
    /// <param name="dryRun">When true, nothing is written and the result reports what would change.</param>
    /// <param name="cancellationToken">Cancels the scan. A cancelled compaction leaves the log untouched.</param>
    public async ValueTask<ChangeLogCompactionResult> CompactChangeLogAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // _changeFeedGate alone, held for the whole operation. The lock order this provider
        // establishes is Gate(address) -> _changeFeedGate (AppendAsync, ImportAsync), so taking the
        // second alone inverts nothing. It does block every address's append for the rewrite, which
        // is exactly why this is a method an operator calls and not something PruneAsync does.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompactChangeLogUnsafeAsync(dryRun, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changeFeedGate.Release();
        }
    }

    // The caller must already hold _changeFeedGate.
    private async ValueTask<ChangeLogCompactionResult> CompactChangeLogUnsafeAsync(
        bool dryRun,
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
            // No log means nothing to compact, not an error -- the same reading every other method
            // in this file gives a missing change log.
            return new ChangeLogCompactionResult { DryRun = dryRun };
        }

        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            existing = buffer.ToArray();
        }

        long linesBefore = 0;
        long linesAfter = 0;
        byte[] compacted;

        // Each distinct history file is deserialized ONCE, not once per line. A long log is long
        // because one address was written many times, so without this the compactor would be linear
        // in lines rather than in distinct files -- on the provider whose whole problem is that its
        // log is long.
        var positions = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

        // Never emit two lines with the same decoded content. Not deduplication for its own sake:
        // two identical lines are what would let a compaction shift one into the other's byte offset
        // and leave a partially-scanned reader's tail re-verify (RefreshChangeFeedIndexUnsafeAsync's
        // rule 2) passing on stale content. See the spec's Phase 12 item 5.
        //
        // Keyed on the DECODED line rather than on raw bytes, and that is strictly stronger rather
        // than weaker: two byte-identical lines always decode identically, so the byte-level
        // invariant above holds under this comparison too. The one case the two disagree about is a
        // CRLF-terminated line against an LF-terminated one carrying the same content --
        // DecodeChangeFeedLine trims the carriage return -- and those two parse to the same
        // (position, address, revision), so keeping both would make one record yield twice. Measured
        // in ROADMAP 0.3 Phase 13 over a log holding one logical line as LF, CRLF, LF: this collapses
        // 3 lines to 1 where a raw-byte comparison would leave 2 and a duplicate yield.
        // Compaction_collapses_a_line_repeated_with_both_line_endings pins it.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using (var kept = new MemoryStream(existing.Length))
        {
            int start = 0;
            while (start < existing.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int newline = Array.IndexOf(existing, (byte)'\n', start);
                if (newline < 0)
                {
                    // The unterminated final fragment. Copied through verbatim and never parsed: the
                    // torn-tail invariant is what turns a crash mid-append into a loud, recoverable
                    // corruption report rather than a silent loss, and it is not this method's job
                    // to repair it.
                    kept.Write(existing, start, existing.Length - start);
                    break;
                }

                linesBefore++;
                int length = newline + 1 - start;
                string line = DecodeChangeFeedLine(existing, start, newline - start);
                if (line.Length == 0)
                {
                    // An empty line dereferences nothing and is skipped on read anyway.
                    start = newline + 1;
                    continue;
                }

                if (!TryParseChangeFeedLine(line, out long position, out StateAddress address, out long revision))
                {
                    if (newline + 1 == existing.Length)
                    {
                        // The file's last line, and it does not parse: the case ReadAsync neither
                        // consumes nor reports. Copied through, so the read path keeps its verdict.
                        kept.Write(existing, start, length);
                        linesAfter++;
                        break;
                    }

                    // Only the FINAL line can be a partially-written append, so a malformed line
                    // before the end means the log is corrupt. Same exception, same 1-based number
                    // the read path reports.
                    throw CorruptChangeFeedLine((int)linesBefore, line);
                }

                string historyFile = HistoryFile(address, revision);
                if (!positions.TryGetValue(historyFile, out long? stored))
                {
                    FileRecord? record = await ReadFileAsync(historyFile, cancellationToken).ConfigureAwait(false);
                    stored = record?.GlobalPosition;
                    positions[historyFile] = stored;
                }

                // Two clauses in one comparison. A null `stored` is the dangling line PruneAsync
                // leaves behind: the history file is gone. A different `stored` is the import
                // residue: the revision was re-imported at another position and its history file was
                // rewritten in place, so this line now points at a record that reports a position
                // this line does not carry -- and ReadAsync yields that record twice.
                if (stored != position)
                {
                    start = newline + 1;
                    continue;
                }

                if (!seen.Add(line))
                {
                    start = newline + 1;
                    continue;
                }

                kept.Write(existing, start, length);
                linesAfter++;
                start = newline + 1;
            }

            compacted = kept.ToArray();
        }

        var result = new ChangeLogCompactionResult
        {
            DryRun = dryRun,
            LinesBefore = linesBefore,
            LinesAfter = linesAfter,
            BytesBefore = existing.Length,
            BytesAfter = compacted.Length,
        };

        // Compaction only ever removes whole lines, so equal lengths mean nothing was dropped.
        // Skipping the rewrite is what makes a repeated call cheap and idempotent -- and, once the
        // generation counter lands, what keeps a no-op from discarding every reader's index.
        if (dryRun || compacted.Length == existing.Length)
        {
            return result;
        }

        await BumpChangeFeedGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceChangeFeedFileUnsafeAsync(compacted, cancellationToken).ConfigureAwait(false);

        // Reset rather than patch. The index remembers a byte offset, a line count and the tail
        // bytes of a file that no longer exists; rebuilding from scratch on the next read is the
        // provably-correct choice and costs one re-parse of a file that just got smaller.
        ResetChangeFeedIndexUnsafe();
        return result;
    }

    public async IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
        StateChangeCursor? from,
        StateChangeReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        long since = from?.Position ?? 0;

        bool fileExists;
        ChangeFeedEntry? pending;

        // The index is refreshed under the same gate AppendChangeFeedEntryUnsafeAsync uses to append,
        // so a concurrent read and append in THIS process cannot race for the file handle -- that
        // ordering is a process-local guarantee, not a cross-process one. Opening with
        // FileShare.ReadWrite is what lets a reader in ANOTHER process coexist with this process's
        // append: on Windows the default share mode a plain read grants no Write access, so a
        // concurrent writer's open would otherwise throw IOException.
        //
        // What Phase 11 changed is how much is parsed under it: the bytes appended since the last
        // read, not the whole log. The gate therefore blocks appends for a suffix rather than for a
        // file, which is the second payoff of the index -- the old cost was a write-throughput
        // problem as much as a read-latency one.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (fileExists, pending) = await RefreshChangeFeedIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changeFeedGate.Release();
        }

        if (!fileExists)
        {
            yield break;
        }

        if (pending is ChangeFeedEntry fragment && fragment.Position <= since)
        {
            pending = null;
        }

        // Take bounds records YIELDED, not entries parsed. The two differ here because a prune
        // deletes a history file and leaves its change-log line behind as a dangling entry, which
        // this loop skips. If Take bounded parsed entries, a page made up entirely of dangling
        // entries would come back empty while live records sat above it -- and a paging consumer
        // reads an empty page as "caught up", so its cursor would stall at that position forever.
        int yielded = 0;
        long copiedThrough = since;
        var chunk = new List<ChangeFeedEntry>(ChangeFeedCopyChunk);
        bool more = true;
        while (more)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // _indexEntries is never enumerated outside the gate: a concurrent append inserts into
            // it, and enumerating a List<T> across a mutation is undefined. Each chunk is a bounded
            // copy taken under the gate -- a binary search to the first entry above the last position
            // copied, then at least ChangeFeedCopyChunk entries, extended to the end of any run of
            // entries sharing the chunk's last position so a colliding restore position is never split
            // across two chunks -- so the gate is held for one chunk at a time rather than for the
            // whole yield loop, whose history-file reads are the slow part.
            chunk.Clear();
            await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CopyChangeFeedEntriesUnsafe(copiedThrough, chunk);
            }
            finally
            {
                _changeFeedGate.Release();
            }

            // A full chunk may have more behind it; a short one is the end of the index. "Full" is
            // >= ChangeFeedCopyChunk rather than ==, because a chunk that ends on a colliding position
            // is extended past the constant to avoid splitting that position across chunks -- see
            // CopyChangeFeedEntriesUnsafe.
            more = chunk.Count >= ChangeFeedCopyChunk;
            if (chunk.Count > 0)
            {
                copiedThrough = chunk[^1].Position;
            }

            // The unterminated fragment is merged in position order rather than inserted into the
            // index: it is not durable yet, and consuming it would leave the index remembering half a
            // line. Its position is usually the highest -- it is the file's last line -- but an
            // import in flight can carry a lower one, so it is placed rather than appended.
            if (pending is ChangeFeedEntry candidate && (!more || candidate.Position < copiedThrough))
            {
                int at = chunk.FindIndex(entry => entry.Position > candidate.Position);
                chunk.Insert(at < 0 ? chunk.Count : at, candidate);
                pending = null;
            }

            if (chunk.Count == 0)
            {
                break;
            }

            foreach (ChangeFeedEntry entry in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileRecord? record = await ReadFileAsync(HistoryFile(entry.Address, entry.Revision), cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    continue;
                }

                yield return new StateChangeEnvelope
                {
                    Record = record.ToStateRecord(),
                    Cursor = new StateChangeCursor(entry.Position),
                };

                if (options.Take is int take && ++yielded >= take)
                {
                    yield break;
                }
            }
        }
    }

    public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var latest = new Dictionary<string, (StateAddress Address, long Position)>(StringComparer.Ordinal);

        // Read under the same gate AppendChangeFeedEntryUnsafeAsync uses to append, for the same
        // reason ReadAsync does (see the comment there): the gate orders this process's readers
        // against this process's writer, and the FileShare.ReadWrite open in
        // ReadChangeFeedLinesAsync is what admits a reader in another process alongside it.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (_, IReadOnlyList<string> lines) = await ReadChangeFeedLinesAsync(cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseChangeFeedLine(line, out long position, out StateAddress address, out long _))
                {
                    if (index == lines.Count - 1)
                    {
                        break;
                    }

                    throw CorruptChangeFeedLine(index + 1, line);
                }

                if (!latest.TryGetValue(address.Canonical, out (StateAddress Address, long Position) existing) ||
                    position > existing.Position)
                {
                    latest[address.Canonical] = (address, position);
                }
            }
        }
        finally
        {
            _changeFeedGate.Release();
        }

        foreach ((StateAddress address, long position) in latest.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StatePartitionDescriptor
            {
                Address = address,
                LastPosition = new StateChangeCursor(position),
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (SemaphoreSlim gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
        _changeFeedGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private SemaphoreSlim Gate(StateAddress address) =>
        _gates.GetOrAdd(address.Canonical, _ => new SemaphoreSlim(1, 1));

    private async ValueTask<StateRecord?> ReadLatestUnsafeAsync(
        StateAddress address,
        CancellationToken cancellationToken)
    {
        FileRecord? head = await ReadFileAsync(HeadFile(address), cancellationToken).ConfigureAwait(false);
        string historyDirectory = HistoryDirectory(address);
        FileRecord? recovered = null;
        if (Directory.Exists(historyDirectory))
        {
            string? latestFile = Directory.EnumerateFiles(historyDirectory, "*.json")
                .OrderByDescending(value => value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latestFile is not null)
            {
                recovered = await ReadFileAsync(latestFile, cancellationToken).ConfigureAwait(false);
            }
        }

        FileRecord? latest = head is null || (recovered?.Revision ?? 0) > head.Revision ? recovered : head;
        return latest?.ToStateRecord();
    }

    private async ValueTask<FileRecord?> ReadFileAsync(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        // File.Exists above and the open below are not atomic: a concurrent PruneAsync deleting
        // this same file in that window would otherwise surface FileNotFoundException out of the
        // feed enumerator instead of the documented "dangling entry is skipped silently".
        //
        // FileShare.Read | FileShare.Delete, rather than File.OpenRead's FileShare.Read alone. Delete
        // is what lets a concurrent PruneAsync unlink this file and a concurrent ImportAsync rename
        // over it while this handle is open; without it both fail on Windows, because a delete and a
        // POSIX-semantics rename each need FILE_SHARE_DELETE from every live handle. Read is NOT
        // widened to ReadWrite: what this open admits is unchanged, only what may be done to the file
        // underneath it.
        FileStream stream;
        try
        {
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<FileRecord>(stream, _json, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AtomicWriteAsync(string file, FileRecord record, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(file);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = _flushToDisk ? FileOptions.WriteThrough : FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, record, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            // Not File.Move(..., overwrite: true): on Windows that fails with ERROR_ACCESS_DENIED
            // against a destination any process holds open, and a feed read holds every history file
            // it yields open for the length of one deserialization. See ReplaceFileOverOpenReaders.
            ReplaceFileOverOpenReaders(temporary, file);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string StreamDirectory(StateAddress address)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Canonical))).ToLowerInvariant();
        return Path.Combine(_rootDirectory, hash[..2], hash);
    }

    private string HistoryDirectory(StateAddress address) => Path.Combine(StreamDirectory(address), "history");

    private string HeadFile(StateAddress address) => Path.Combine(StreamDirectory(address), "head.json");

    private string HistoryFile(StateAddress address, long revision) =>
        Path.Combine(HistoryDirectory(address), revision.ToString("D20", CultureInfo.InvariantCulture) + ".json");

    private string ChangeFeedFile => Path.Combine(_rootDirectory, "_changes.log");

    private string ChangeFeedGenerationFile => Path.Combine(_rootDirectory, "_changes.gen");

    // A change-log line is exactly five tab-separated fields. Because File.AppendAllTextAsync is not
    // atomic, a reader -- in this process or another one -- can observe the FINAL line as a prefix
    // of a real one. That is the only line a tear can produce: every later line was written after
    // the torn one's append had completed. So callers skip a malformed final line and report any
    // other one, rather than blanket-skipping, which would quietly drop records from a feed
    // documented as lossless within retention.
    private static bool TryParseChangeFeedLine(
        string line,
        out long position,
        out StateAddress address,
        out long revision)
    {
        position = 0;
        revision = 0;
        address = default;

        string[] fields = line.Split('\t');
        if (fields.Length != 5 ||
            !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out position) ||
            !long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision))
        {
            position = 0;
            revision = 0;
            return false;
        }

        address = new StateAddress(fields[1], new StatePath(fields[2]), new StatePartition(fields[3]));
        return true;
    }

    private InvalidDataException CorruptChangeFeedLine(int lineNumber, string line) =>
        new($"The change log '{ChangeFeedFile}' has a malformed entry at line {lineNumber}: '{line}'. " +
            "Only the final line of the log can be a partially-written append; a malformed line before " +
            "the end means the log is corrupt.");

    // The caller must already hold _changeFeedGate. AtomicWriteAsync's shape, for a byte array
    // rather than a FileRecord: the temporary MUST live in _rootDirectory, because replacing a file
    // by renaming over it is an atomic directory-entry swap only within one volume and a temp under
    // TEMP could be on another. ChangeFeedFile is already in _rootDirectory, so appending a suffix
    // to it keeps the temp there too -- the same trick AtomicWriteAsync uses.
    private async ValueTask ReplaceChangeFeedFileUnsafeAsync(byte[] contents, CancellationToken cancellationToken)
    {
        string temporary = ChangeFeedFile + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = _flushToDisk ? FileOptions.WriteThrough : FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            ReplaceFileOverOpenReaders(temporary, ChangeFeedFile);
        }
        finally
        {
            // Only ever the temp THIS call created. A stale temp from a crashed compaction is left
            // alone: deleting a file this method did not write would be a repair, and compaction is
            // not a repair tool.
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // The caller must already hold _changeFeedGate. A missing, empty or unparseable sidecar reads as
    // zero, which is what makes every store directory written before this file existed compatible
    // with no migration -- and it is the safe direction: a directory that later gains the file
    // forces one spurious index discard rather than one silent stale read.
    private async ValueTask<long> ReadChangeFeedGenerationUnsafeAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                ChangeFeedGenerationFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }

        await using (stream.ConfigureAwait(false))
        {
            // One decimal long plus a newline is at most 21 bytes; 32 is the whole file with room to
            // spare, so one read is the whole file and a short read is not a partial parse.
            byte[] buffer = new byte[32];
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return read > 0 &&
                   long.TryParse(
                       Encoding.UTF8.GetString(buffer, 0, read).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out long generation)
                ? generation
                : 0;
        }
    }

    // The caller must already hold _changeFeedGate. Bumped BEFORE the rename, never after: a
    // generation raised after the log was replaced leaves a window in which a compacted log carries
    // an unchanged generation, and a reader that looked in that window would keep an index built
    // from the old file. Crashing between the bump and the rename leaves a raised generation over an
    // uncompacted log, which costs one spurious index discard -- the safe direction.
    //
    // The sidecar is replaced through ReplaceFileOverOpenReaders for the same reason the log is, and
    // more urgently: the seqlock reads this file twice per ReadAsync, so a cross-process reader holds
    // THIS name open far more often than it holds the log open. File.Move(..., overwrite: true) fails
    // with ERROR_ACCESS_DENIED against a destination any process has open, FileShare.Delete or not.
    private async ValueTask BumpChangeFeedGenerationUnsafeAsync(CancellationToken cancellationToken)
    {
        long next = await ReadChangeFeedGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false) + 1;
        byte[] contents = Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture) + "\n");
        string temporary = ChangeFeedGenerationFile + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = _flushToDisk ? FileOptions.WriteThrough : FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            ReplaceFileOverOpenReaders(temporary, ChangeFeedGenerationFile);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // Replace `destination` with `source` in ONE directory operation, atomically, even while another
    // process holds `destination` open for reading.
    //
    // On Unix, File.Move(overwrite: true) is rename(2) and already does exactly that: the old inode
    // is detached from the name and every open descriptor keeps reading it.
    //
    // On Windows, File.Move(overwrite: true) is MoveFileEx(MOVEFILE_REPLACE_EXISTING), and that
    // fails with ERROR_ACCESS_DENIED against a destination ANY process holds open -- FileShare.Delete
    // does not change that, which was measured rather than assumed. The classic replace has to take
    // the destination's NAME out of the directory, and while a handle is open the file system can
    // only mark the file delete-pending and leave the name where it is, so the rename has nowhere to
    // land. FILE_RENAME_FLAG_POSIX_SEMANTICS is the flag that detaches the name immediately, which is
    // the Unix behaviour this provider's readers already assume. It needs Windows 10 1607 / Server
    // 2016 or newer on a file system that implements the class -- NTFS does, FAT and the SMB
    // redirector do not -- and it still requires every open handle on the destination to have granted
    // FILE_SHARE_DELETE, which is exactly what this provider's three change-log read opens do.
    //
    // Where the class is not implemented this falls back to File.Move(overwrite: true), which is
    // correct whenever nothing holds the destination open and throws loudly when something does.
    // Compaction is a maintenance call an operator makes, so "retry with no reader attached" is a
    // usable answer; half-replacing a log a reader is mid-way through is not.
    private static void ReplaceFileOverOpenReaders(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && TryReplaceByPosixRename(source, destination))
        {
            return;
        }

        File.Move(source, destination, overwrite: true);
    }

    // Returns false, having changed nothing, only when this Windows build or file system does not
    // implement FileRenameInfoEx. Every other failure throws: a sharing violation here means someone
    // opened the destination without FileShare.Delete, and falling back to a call that cannot succeed
    // either would only replace one error with a more confusing one.
    [SupportedOSPlatform("windows")]
    private static bool TryReplaceByPosixRename(string source, string destination)
    {
        const uint DeleteAccess = 0x00010000;
        const uint Synchronize = 0x00100000;
        const uint ShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint OpenExisting = 3;
        const uint NormalAttributes = 0x00000080;
        const int FileRenameInfoEx = 22;
        const uint ReplaceIfExists = 0x00000001;
        const uint PosixSemantics = 0x00000002;
        const int ErrorInvalidFunction = 1;
        const int ErrorNotSupported = 50;
        const int ErrorInvalidParameter = 87;

        using SafeFileHandle handle = Interop.CreateFileW(
            source,
            DeleteAccess | Synchronize,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            NormalAttributes,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw ReplaceFailure(Marshal.GetLastWin32Error(), source);
        }

        // FILE_RENAME_INFO is a variable-length structure C# cannot declare: a 4-byte flags union
        // padded up to pointer alignment, a pointer-sized RootDirectory, a 4-byte FileNameLength in
        // BYTES, then the UTF-16 name inline. The offsets are derived from IntPtr.Size rather than
        // hard-coded, because a 32-bit process lays the same structure out eight bytes shorter. The
        // name must be fully qualified; every caller's path already is, and GetFullPath keeps that
        // from being a silent requirement.
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int rootDirectoryOffset = IntPtr.Size;
        int fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        int headerLength = fileNameLengthOffset + sizeof(int);
        int length = headerLength + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            for (int i = 0; i < length; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, (int)(ReplaceIfExists | PosixSemantics));
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + headerLength, name.Length);

            if (Interop.SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)length))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            if (error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter)
            {
                return false;
            }

            throw ReplaceFailure(error, destination);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // An IOException naming the path, because that is what a caller of a file-replacing method can
    // act on. The HRESULT keeps the Win32 code recoverable for anyone who needs it.
    private static IOException ReplaceFailure(int error, string path) =>
        new(
            $"Could not replace '{path}': {new Win32Exception(error).Message}",
            unchecked((int)(0x80070000 | (uint)error)));

    private static class Interop
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);
    }

    // The caller must already hold _changeFeedGate. SemaphoreSlim is not reentrant, so appending
    // the change-log line has to be callable from inside the widened critical section that
    // AppendAsync and ImportAsync now open. The name mirrors this file's existing
    // ReadLatestUnsafeAsync convention: "Unsafe" means "the caller holds the lock", not "unsound".
    private async ValueTask AppendChangeFeedEntryUnsafeAsync(StateRecord record, CancellationToken cancellationToken)
    {
        string line = string.Join(
            '\t',
            record.GlobalPosition.ToString(CultureInfo.InvariantCulture),
            record.Address.Root,
            record.Address.Path.Value,
            record.Address.Partition.Value,
            record.Revision.ToString(CultureInfo.InvariantCulture)) + "\n";

        // A crash mid-append leaves the log ending in a partial line with no terminating newline.
        // Appending straight onto that prefix would merge this record into it, and the merged line
        // is still the FINAL line -- so ReadAsync's torn-tail guard would skip it and this
        // committed record would be missing from a feed documented as lossless, with AppendAsync
        // still reporting success. Starting a fresh line instead leaves the torn prefix as a
        // malformed line that is no longer last, which ReadAsync reports as corruption naming the
        // line: a loud, recoverable failure rather than a silent loss. The cost is one extra open
        // of a file this method is about to open anyway, on the provider whose append is already
        // fsync-bound.
        // The check and the append below are two file opens under _changeFeedGate, which is
        // process-local -- so two PROCESSES appending to one change log can interleave between them.
        // That is by design rather than an oversight: this provider's writer side is single-process
        // because _globalPosition lives in memory and is never seeded from the existing log (see
        // docs/providers/index.md), so a cross-process append lock would close a byte-interleaving
        // symptom while the position allocator, which the feed's guarantee actually depends on, stayed
        // single-process. Making multi-process writers work is a feature, not a fix here.
        if (!await ChangeFeedEndsWithNewlineAsync(cancellationToken).ConfigureAwait(false))
        {
            line = "\n" + line;
        }

        // Mirrors AtomicWriteAsync's flush pattern, so FlushToDisk means one thing across this
        // provider rather than covering the history and head writes and silently skipping the log --
        // which is the structure the change feed's commit point actually is. The cost is real and
        // measured: this fsync sits INSIDE _changeFeedGate, so the globally serialized portion of an
        // append goes from one fsync to two. See docs/providers/index.md.
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        var appendOptions = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = _flushToDisk ? FileOptions.WriteThrough : FileOptions.Asynchronous,
        };
        await using (var stream = new FileStream(ChangeFeedFile, appendOptions))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_flushToDisk)
            {
                stream.Flush(flushToDisk: true);
            }
        }
    }

    // The caller must already hold _changeFeedGate. FileShare.ReadWrite for the same reason
    // ReadChangeFeedLinesAsync uses it: a reader or writer in another process must be able to hold
    // the file open across this one-byte read. A missing or empty log needs no separator, so both
    // answer "yes" -- the append writes the first line of the file either way.
    // ... and FileShare.Delete, without which a compaction cannot rename over this file at all.
    private async ValueTask<bool> ChangeFeedEndsWithNewlineAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                ChangeFeedFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                useAsync: true);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length == 0)
            {
                return true;
            }

            stream.Seek(-1, SeekOrigin.End);
            byte[] last = new byte[1];
            int read = await stream.ReadAsync(last, cancellationToken).ConfigureAwait(false);
            return read == 1 && last[0] == (byte)'\n';
        }
    }

    // How many times a refresh will re-read after observing a compaction mid-flight. This is not a
    // "two tiny reads" retry: a generation mismatch at the top of an iteration discards the index and
    // hands the core refresh nothing to resume from, so that iteration reparses the WHOLE log from
    // byte zero rather than an incremental suffix. Under a compactor running at least once per
    // iteration, one ReadAsync call can pay up to MaxChangeFeedGenerationRetries + 1 full-log
    // reparses before it gives up and returns the last refresh -- size this against the real log,
    // not against the seqlock's own two 32-byte generation reads.
    private const int MaxChangeFeedGenerationRetries = 3;

    // The caller must already hold _changeFeedGate. Wraps the index refresh in a seqlock over the
    // compaction generation: read it, refresh, read it again, and discard plus retry if it moved.
    //
    // _changeFeedGate orders THIS process's readers against THIS process's writer, so the window
    // this closes is the cross-process one -- compaction runs in the writer's process (it has to;
    // see CompactChangeLogAsync) while a reader in another process is mid-refresh.
    //
    // Cost, stated honestly (see MaxChangeFeedGenerationRetries): the two
    // ReadChangeFeedGenerationUnsafeAsync calls per iteration are cheap only when the generation held
    // still. A mismatch forces ResetChangeFeedIndexUnsafe() and a full reparse for that iteration --
    // the expensive path this seqlock exists to fall back to, not a marginal cost on top of it.
    //
    // What the generation buys over the three rules below it: those rules are sufficient only
    // because CompactChangeLogAsync never emits two byte-identical lines, and because the log's
    // lines have the widths they currently have. Both are true today and both are checkable, but
    // neither is visible from here. The generation makes the index's validity a property of the file
    // rather than of an argument about the file's contents, so a later change to the line format or
    // to the compactor cannot quietly reintroduce a stale read in a second process.
    private async ValueTask<(bool Exists, ChangeFeedEntry? Tail)> RefreshChangeFeedIndexUnsafeAsync(
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            long before = await ReadChangeFeedGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (before != _indexGeneration)
            {
                ResetChangeFeedIndexUnsafe();
                _indexGeneration = before;
            }

            (bool exists, ChangeFeedEntry? tail) =
                await RefreshChangeFeedIndexCoreUnsafeAsync(cancellationToken).ConfigureAwait(false);
            long after = await ReadChangeFeedGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (after == before || attempt >= MaxChangeFeedGenerationRetries)
            {
                // Out of retries means a compactor is running far more often than this reader reads.
                // Taking the last refresh is no worse than the behaviour before this seqlock existed,
                // and the next read sees a settled generation. Looping forever instead would turn a
                // busy maintenance schedule into a hang.
                //
                // _indexGeneration is settled to `after`, the freshest generation number this call
                // observed, rather than left at `before` (or whatever it held coming in): this call is
                // already returning without having matched `before` to `after`, so recording `before`
                // would only guarantee the very next call sees the same stale mismatch again and pays
                // another full reparse for a change this call has already accepted.
                _indexGeneration = after;
                return (exists, tail);
            }

            ResetChangeFeedIndexUnsafe();
            _indexGeneration = after;
        }
    }

    // The caller must already hold _changeFeedGate. Brings the index up to date with the file and
    // returns (does the file exist, this read's transient unterminated-tail candidate).
    //
    // Three invalidation rules:
    //   1. A file shorter than _indexScannedBytes shrank -- truncated by the documented corruption
    //      recovery, replaced, or compacted by a tool that does not exist yet. This is a fast path,
    //      not the check that carries correctness: it skips a doomed seek-and-read on a file already
    //      known to be shorter than the remembered offset. Rule 2 below independently catches every
    //      shrink anyway, this one included -- whenever anything has been consumed, _indexTail is
    //      non-empty, and reading it back at the remembered offset hits EOF on any shorter file and
    //      reports a mismatch. Removing rule 1 changes no observable behavior; it only trades a cheap
    //      length comparison for an always-failing read.
    //   2. A same-length rewrite of the tail is what a length check alone cannot see, so the
    //      remembered tail bytes are re-read at their remembered offset and compared on every read. A
    //      mismatch discards too. This is the check that actually carries correctness for every
    //      shrink and every same-length rewrite of the tail; a same-length rewrite of an earlier line
    //      is undetected, but neither documented shrink scenario (corruption truncation, future
    //      compaction) rewrites in place without also shrinking, so nothing real is missed.
    //   3. An unterminated final fragment is never consumed; see ConsumeChangeFeedSuffixUnsafe.
    private async ValueTask<(bool Exists, ChangeFeedEntry? Tail)> RefreshChangeFeedIndexCoreUnsafeAsync(
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            // FileShare.Delete is what lets CompactChangeLogAsync replace this file while this handle
            // is open. Windows will not let a POSIX-semantics rename take over a name any process
            // holds open without FILE_SHARE_DELETE, and without POSIX semantics it will not take it
            // over at all -- see ReplaceFileOverOpenReaders. Granting Delete gives this reader the
            // behaviour it already assumes, where an open handle keeps reading the old, unlinked file.
            stream = new FileStream(ChangeFeedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A missing file -- never written to, or deleted by something outside this store between
            // calls -- means "no feed yet", not an error. Anything the index remembers about a file
            // that is gone is worthless.
            ResetChangeFeedIndexUnsafe();
            return (false, null);
        }

        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length < _indexScannedBytes)
            {
                ResetChangeFeedIndexUnsafe();
            }
            else if (_indexTail.Length > 0)
            {
                byte[] tail = new byte[_indexTail.Length];
                stream.Seek(_indexScannedBytes - _indexTail.Length, SeekOrigin.Begin);

                // throwOnEndOfStream: false is deliberate, and is the whole reason this is not a
                // plain ReadExactlyAsync. A concurrent truncation between the Length read above and
                // this read makes it return a short count, and a short count is treated as a
                // mismatch -- which discards the index, the safe direction. The default (true) would
                // surface an IOException out of a feed read instead.
                int read = await stream
                    .ReadAtLeastAsync(tail, tail.Length, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);
                if (read < tail.Length || !tail.AsSpan().SequenceEqual(_indexTail))
                {
                    ResetChangeFeedIndexUnsafe();
                }
            }

            // Read to end-of-file rather than to the Length observed above: under FileShare.ReadWrite
            // the length can change under this handle, so "everything after the offset" is the honest
            // request, and it is what keeps "the last line I see is the file's last line" true and
            // the torn-tail rule intact. The Length is used only to SIZE the buffer, which is a hint
            // and not a bound -- CopyToAsync still reads to the end whatever it finds there.
            stream.Seek(_indexScannedBytes, SeekOrigin.Begin);
            long remaining = stream.Length - _indexScannedBytes;
            using var buffer = new MemoryStream(remaining > 0 && remaining <= int.MaxValue ? (int)remaining : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            // GetBuffer rather than ToArray: the suffix has already been copied into this stream's
            // array once, and ToArray copies it out again. The consumer below is given the array and
            // the length instead, so a cold first read on a long log allocates one copy rather than
            // two.
            int suffixLength = (int)buffer.Length;
            return suffixLength == 0
                ? (true, null)
                : (true, ConsumeChangeFeedSuffixUnsafe(buffer.GetBuffer(), suffixLength));
        }
    }

    // The caller must already hold _changeFeedGate. Consumes every COMPLETE line in the suffix into
    // the index and returns the file's unterminated final fragment as a transient candidate.
    //
    // Only terminator-present lines are consumed. An unterminated fragment is parsed for this read
    // and then forgotten, with _indexScannedBytes left before it, so the next read parses it whole
    // once it is complete -- which is what stops an append observed in flight from being remembered
    // as half a line forever. Both of today's outcomes survive: a complete-but-unterminated final
    // line yields, and a torn one is skipped.
    private ChangeFeedEntry? ConsumeChangeFeedSuffixUnsafe(byte[] suffix, int suffixLength)
    {
        int start = 0;
        while (start < suffixLength)
        {
            int newline = Array.IndexOf(suffix, (byte)'\n', start, suffixLength - start);
            if (newline < 0)
            {
                string fragment = DecodeChangeFeedLine(suffix, start, suffixLength - start);

                // Not interned: this address may belong to a line that is never consumed, and the
                // intern table exists to bound what the index RETAINS.
                return TryParseChangeFeedLine(fragment, out long tailPosition, out StateAddress tailAddress, out long tailRevision)
                    ? new ChangeFeedEntry(tailPosition, tailAddress, tailRevision)
                    : null;
            }

            string line = DecodeChangeFeedLine(suffix, start, newline - start);
            bool isLastInFile = newline + 1 == suffixLength;
            if (line.Length > 0)
            {
                if (!TryParseChangeFeedLine(line, out long position, out StateAddress address, out long revision))
                {
                    if (isLastInFile)
                    {
                        // The last line in the file, and it does not parse. Neither consumed nor
                        // reported: on the next read it is either still last (nothing changed) or an
                        // append has put a line after it, and then the throw below fires -- with the
                        // number this index has been counting all along, because the line was never
                        // consumed and _indexLineCount never moved past it.
                        return null;
                    }

                    throw CorruptChangeFeedLine(_indexLineCount + 1, line);
                }

                InsertChangeFeedEntryUnsafe(new ChangeFeedEntry(position, InternAddressUnsafe(address), revision));
            }

            // An empty line is skipped, as it always was, but it still counts: the shipped
            // corruption message numbers every line StreamReader produced, empty ones included.
            _indexTail = suffix[start..(newline + 1)];
            _indexScannedBytes += newline + 1 - start;
            _indexLineCount++;
            start = newline + 1;
        }

        return null;
    }

    // The caller must already hold _changeFeedGate. Copies at least ChangeFeedCopyChunk entries whose
    // position is strictly above `after`, in position order. _indexEntries is sorted, so the start is
    // a binary search rather than a scan -- which is what stops a page deep in a long log from
    // costing a walk of everything below it, and is the row of the plan's cost table this method is.
    //
    // Never cuts a chunk in the middle of a position: after the bounded loop, any further entries
    // that share the last copied position are drained too, so the chunk can exceed
    // ChangeFeedCopyChunk by the collision multiplicity. Two entries at one position is a documented,
    // supported shape -- a colliding-lineage restore -- and resuming the NEXT chunk by position value
    // (ReadAsync's copiedThrough) means splitting a shared position across chunks would skip whichever
    // entry landed in the second chunk for good, because the next call starts strictly above that same
    // position. This was a real regression: a chunk boundary landing between two colliding entries
    // dropped the second one from a `Take = null` drain.
    private void CopyChangeFeedEntriesUnsafe(long after, List<ChangeFeedEntry> destination)
    {
        int index = FirstChangeFeedEntryAboveUnsafe(after);
        int end = Math.Min(_indexEntries.Count, index + ChangeFeedCopyChunk);
        for (; index < end; index++)
        {
            destination.Add(_indexEntries[index]);
        }

        while (index < _indexEntries.Count &&
               destination.Count > 0 &&
               _indexEntries[index].Position == destination[^1].Position)
        {
            destination.Add(_indexEntries[index++]);
        }
    }

    // The caller must already hold _changeFeedGate. The index of the first entry whose position is
    // strictly greater than `position`, or Count when there is none.
    private int FirstChangeFeedEntryAboveUnsafe(long position)
    {
        int low = 0;
        int high = _indexEntries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_indexEntries[middle].Position <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // The caller must already hold _changeFeedGate. An append's position is the highest so far, so it
    // binary-searches to Count and takes the O(1) Add path; an import at a lower position pays the
    // List<T>.Insert copy, which is correct and rare.
    private void InsertChangeFeedEntryUnsafe(ChangeFeedEntry entry)
    {
        int at = FirstChangeFeedEntryAboveUnsafe(entry.Position);
        if (at == _indexEntries.Count)
        {
            _indexEntries.Add(entry);
        }
        else
        {
            _indexEntries.Insert(at, entry);
        }
    }

    // The caller must already hold _changeFeedGate.
    private StateAddress InternAddressUnsafe(StateAddress address)
    {
        if (_indexAddresses.TryGetValue(address, out StateAddress existing))
        {
            return existing;
        }

        _indexAddresses[address] = address;
        return address;
    }

    // The caller must already hold _changeFeedGate.
    private void ResetChangeFeedIndexUnsafe()
    {
        _indexEntries.Clear();
        _indexAddresses.Clear();
        _indexScannedBytes = 0;
        _indexLineCount = 0;
        _indexTail = [];
    }

    // One change-log line as text, with a \r\n terminator's carriage return removed. StreamReader
    // treated \r\n as one terminator and the parse stays tolerant of a log written on a platform that
    // produced them. It deliberately does NOT split on a bare \r the way StreamReader does: the
    // shipped writer never emits one (An_append_to_a_well_formed_log_adds_no_blank_line asserts the
    // log contains no \r at all), and splitting on it would make a \r inside a field a line break.
    private static string DecodeChangeFeedLine(byte[] buffer, int start, int length)
    {
        if (length > 0 && buffer[start + length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(buffer, start, length);
    }

    // The caller must already hold _changeFeedGate, which orders this read against this process's
    // own writer. FileShare.ReadWrite is what lets a reader in ANOTHER process open the same file
    // concurrently with this process's append: on Windows, File.OpenRead's default share mode
    // grants no Write access, so a concurrent writer's open would otherwise throw IOException. A
    // missing file -- never written to, or deleted by something outside this store between calls
    // -- means "no feed yet", not an error.
    // ... and FileShare.Delete, without which a compaction cannot rename over this file at all.
    private async ValueTask<(bool Exists, IReadOnlyList<string> Lines)> ReadChangeFeedLinesAsync(
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(ChangeFeedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (false, []);
        }

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lines.Add(line);
        }

        return (true, lines);
    }

    private long NextGlobalPosition()
    {
        long timestamp = _timeProvider.GetUtcNow().UtcDateTime.Ticks;
        long current;
        do
        {
            current = Volatile.Read(ref _globalPosition);
            timestamp = Math.Max(timestamp, current + 1);
        }
        while (Interlocked.CompareExchange(ref _globalPosition, timestamp, current) != current);

        return timestamp;
    }

    private void AdvanceGlobalPosition(long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _globalPosition);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _globalPosition, value, current) != current);
    }

    private static bool Matches(StateRecord? current, StateWriteCondition condition)
    {
        if (condition.MustBeAbsent)
        {
            return current is null;
        }

        return condition.ExpectedRevision is null || current?.Revision == condition.ExpectedRevision;
    }

    // One parsed change-log line: the three fields the read path needs. The record itself is not
    // held -- it is re-read from its history file per yielded record, which is what keeps the index
    // to a bounded size and is the same choice the pre-index read already made for its per-call
    // tuple list.
    private readonly record struct ChangeFeedEntry(long Position, StateAddress Address, long Revision);

    private sealed record FileRecord
    {
        public FileRecord()
        {
        }

        public FileRecord(StateRecord record)
        {
            Root = record.Address.Root;
            Path = record.Address.Path.Value;
            Partition = record.Address.Partition.Value;
            Revision = record.Revision;
            GlobalPosition = record.GlobalPosition;
            OccurredAt = record.OccurredAt;
            Operation = record.Operation;
            Status = record.Status;
            ValueType = record.ValueType;
            SchemaVersion = record.SchemaVersion;
            Payload = record.Payload?.ToArray();
            FreshUntil = record.FreshUntil;
            ServeUntil = record.ServeUntil;
            Source = record.Source;
            CorrelationId = record.CorrelationId;
            CausationId = record.CausationId;
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
            Error = record.Error;
        }

        public string Root { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Partition { get; init; } = StatePartition.Default.Value;
        public long Revision { get; init; }
        public long GlobalPosition { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public StateOperation Operation { get; init; }
        public StateStatus Status { get; init; }
        public string ValueType { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public byte[]? Payload { get; init; }
        public DateTimeOffset? FreshUntil { get; init; }
        public DateTimeOffset? ServeUntil { get; init; }
        public string Source { get; init; } = string.Empty;
        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
        public StateError? Error { get; init; }

        public StateRecord ToStateRecord() => new()
        {
            Address = new StateAddress(Root, new StatePath(Path), new StatePartition(Partition)),
            Revision = Revision,
            GlobalPosition = GlobalPosition,
            OccurredAt = OccurredAt,
            Operation = Operation,
            Status = Status,
            ValueType = ValueType,
            SchemaVersion = SchemaVersion,
            Payload = Payload?.ToArray(),
            FreshUntil = FreshUntil,
            ServeUntil = ServeUntil,
            Source = Source,
            CorrelationId = CorrelationId,
            CausationId = CausationId,
            Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase),
            Error = Error,
        };
    }
}
