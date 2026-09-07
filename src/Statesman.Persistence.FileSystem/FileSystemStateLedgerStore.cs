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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long since = from?.Position ?? 0;
        string file = ChangeFeedFile;
        List<(long Position, StateAddress Address, long Revision)> entries = [];
        bool fileExists;

        // Read the change feed under the same gate that AppendChangeFeedEntryAsync uses to
        // append to it, so a concurrent read and append cannot race for the file handle. On
        // Windows, a reader's open (default FileShare.Read) does not grant the Write access a
        // simultaneous writer's open needs, so without this gate the writer's open can throw
        // IOException while a read is in flight.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fileExists = File.Exists(file);
            if (fileExists)
            {
                string[] lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                foreach (string line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] fields = line.Split('\t');
                    long position = long.Parse(fields[0], CultureInfo.InvariantCulture);
                    if (position <= since)
                    {
                        continue;
                    }

                    var address = new StateAddress(fields[1], new StatePath(fields[2]), new StatePartition(fields[3]));
                    long revision = long.Parse(fields[4], CultureInfo.InvariantCulture);
                    entries.Add((position, address, revision));
                }
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
        }
    }

    public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string file = ChangeFeedFile;
        var latest = new Dictionary<string, (StateAddress Address, long Position)>(StringComparer.Ordinal);

        // Read under the same gate AppendChangeFeedEntryAsync uses to append, for the same reason
        // ReadAsync does (see the comment there): a concurrent writer's open needs Write access this
        // reader's default-share open would otherwise block on Windows.
        await _changeFeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(file))
            {
                string[] lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                foreach (string line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] fields = line.Split('\t');
                    long position = long.Parse(fields[0], CultureInfo.InvariantCulture);
                    var address = new StateAddress(fields[1], new StatePath(fields[2]), new StatePartition(fields[3]));
                    if (!latest.TryGetValue(address.Canonical, out (StateAddress Address, long Position) existing) ||
                        position > existing.Position)
                    {
                        latest[address.Canonical] = (address, position);
                    }
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

        await using FileStream stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<FileRecord>(stream, _json, cancellationToken).ConfigureAwait(false);
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

        await File.AppendAllTextAsync(ChangeFeedFile, line, cancellationToken).ConfigureAwait(false);
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
