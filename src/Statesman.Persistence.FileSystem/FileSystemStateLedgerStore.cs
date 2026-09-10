using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman;

public sealed class FileSystemStateLedgerStoreOptions
{
    public required string RootDirectory { get; set; }

    public bool FlushToDisk { get; set; } = true;
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
        FileStream stream;
        try
        {
            stream = File.OpenRead(file);
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

            File.Move(temporary, file, overwrite: true);
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
    private async ValueTask<bool> ChangeFeedEndsWithNewlineAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                ChangeFeedFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
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
    private async ValueTask<(bool Exists, ChangeFeedEntry? Tail)> RefreshChangeFeedIndexUnsafeAsync(
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(ChangeFeedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                if (!await TryReadExactlyAsync(stream, tail, cancellationToken).ConfigureAwait(false) ||
                    !tail.AsSpan().SequenceEqual(_indexTail))
                {
                    ResetChangeFeedIndexUnsafe();
                }
            }

            // Read to end-of-file rather than to the Length observed above: under FileShare.ReadWrite
            // the length can change under this handle, so "everything after the offset" is the honest
            // request. This is the same to-EOF shape ReadChangeFeedLinesAsync uses, which is what
            // keeps "the last line I see is the file's last line" true and the torn-tail rule intact.
            stream.Seek(_indexScannedBytes, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] suffix = buffer.ToArray();
            return suffix.Length == 0 ? (true, null) : (true, ConsumeChangeFeedSuffixUnsafe(suffix));
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
    private ChangeFeedEntry? ConsumeChangeFeedSuffixUnsafe(byte[] suffix)
    {
        int start = 0;
        while (start < suffix.Length)
        {
            int newline = Array.IndexOf(suffix, (byte)'\n', start);
            if (newline < 0)
            {
                string fragment = DecodeChangeFeedLine(suffix, start, suffix.Length - start);

                // Not interned: this address may belong to a line that is never consumed, and the
                // intern table exists to bound what the index RETAINS.
                return TryParseChangeFeedLine(fragment, out long tailPosition, out StateAddress tailAddress, out long tailRevision)
                    ? new ChangeFeedEntry(tailPosition, tailAddress, tailRevision)
                    : null;
            }

            string line = DecodeChangeFeedLine(suffix, start, newline - start);
            bool isLastInFile = newline + 1 == suffix.Length;
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

    // Reads exactly buffer.Length bytes, or reports false when the file ended first -- which a
    // concurrent truncation between the Length read and this read can produce. False is treated as a
    // mismatch by the only caller, which is the safe direction: it discards the index.
    private static async ValueTask<bool> TryReadExactlyAsync(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    // The caller must already hold _changeFeedGate, which orders this read against this process's
    // own writer. FileShare.ReadWrite is what lets a reader in ANOTHER process open the same file
    // concurrently with this process's append: on Windows, File.OpenRead's default share mode
    // grants no Write access, so a concurrent writer's open would otherwise throw IOException. A
    // missing file -- never written to, or deleted by something outside this store between calls
    // -- means "no feed yet", not an error.
    private async ValueTask<(bool Exists, IReadOnlyList<string> Lines)> ReadChangeFeedLinesAsync(
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(ChangeFeedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
