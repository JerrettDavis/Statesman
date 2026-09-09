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
        List<(long Position, StateAddress Address, long Revision)> entries = [];
        bool fileExists;

        // Read the change feed under the same gate that AppendChangeFeedEntryUnsafeAsync uses to
        // append to it, so a concurrent read and append in THIS process cannot race for the file
        // handle -- that ordering is a process-local guarantee, not a cross-process one. Opening
        // with FileShare.ReadWrite is what lets a reader in ANOTHER process coexist with this
        // process's append: on Windows the default share mode a plain read grants no Write access,
        // so a concurrent writer's open would otherwise throw IOException.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (fileExists, IReadOnlyList<string> lines) = await ReadChangeFeedLinesAsync(cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseChangeFeedLine(line, out long position, out StateAddress address, out long revision))
                {
                    if (index == lines.Count - 1)
                    {
                        // A torn tail. Skipping it is right only for an IN-FLIGHT tear -- a reader
                        // in another process observing this process's non-atomic append -- where
                        // the line completes on its own and the next read sees all of it. A tear
                        // that is already DURABLE (what a crash mid-append leaves on disk) never
                        // completes: AppendChangeFeedEntryUnsafeAsync starts a fresh line rather
                        // than merging into it, so the next append stops this line being last and
                        // the throw below reports it. Recover by truncating the partial line.
                        break;
                    }

                    throw CorruptChangeFeedLine(index + 1, line);
                }

                if (position <= since)
                {
                    continue;
                }

                entries.Add((position, address, revision));
            }
        }
        finally
        {
            _changeFeedGate.Release();
        }

        if (!fileExists)
        {
            yield break;
        }

        // Take bounds records YIELDED, not entries parsed. The two differ here because a prune
        // deletes a history file and leaves its change-log line behind as a dangling entry, which
        // this loop skips. If Take bounded parsed entries, a page made up entirely of dangling
        // entries would come back empty while live records sat above it -- and a paging consumer
        // reads an empty page as "caught up", so its cursor would stall at that position forever.
        // The cost of the honest rule is that the whole log is still scanned to find the page: that
        // is a storage property of an append-only text log, documented in docs/providers/index.md,
        // not a process-local fallback for the parameter.
        int yielded = 0;
        foreach ((long position, StateAddress address, long revision) in entries.OrderBy(entry => entry.Position))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileRecord? record = await ReadFileAsync(HistoryFile(address, revision), cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            yield return new StateChangeEnvelope
            {
                Record = record.ToStateRecord(),
                Cursor = new StateChangeCursor(position),
            };

            if (options.Take is int take && ++yielded >= take)
            {
                yield break;
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
        if (!await ChangeFeedEndsWithNewlineAsync(cancellationToken).ConfigureAwait(false))
        {
            line = "\n" + line;
        }

        await File.AppendAllTextAsync(ChangeFeedFile, line, cancellationToken).ConfigureAwait(false);
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
