using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Statesman.Outbox;

/// <summary>
/// An <see cref="IOutboxCursorStore"/> keeping one small JSON file per outbox id under a directory.
/// </summary>
/// <remarks>
/// The file name is the lower-case SHA-256 hex of the outbox id plus <c>.cursor</c>, matching how
/// <c>FileSystemStateLedgerStore</c> names stream directories — so no outbox id, however punctuated,
/// can produce an invalid path. Writes go to a sibling temporary file and are moved into place, so a
/// crash mid-write leaves the previous cursor intact rather than a truncated one. Monotonicity is
/// enforced under a per-outbox in-process gate, which makes concurrent writers <em>in one process</em>
/// converge on the maximum; it does not coordinate across processes, so this store is correct for a
/// single-writer deployment (which is what <see cref="OutboxOptions.RequireLease"/> exists to
/// guarantee) and not for a shared network directory.
/// </remarks>
public sealed class FileSystemOutboxCursorStore : IOutboxCursorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly string _directory;

    /// <summary>Creates the store, creating <paramref name="directory"/> if it does not exist.</summary>
    public FileSystemOutboxCursorStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);

        long position = await ReadPositionAsync(CursorFile(outboxId), cancellationToken).ConfigureAwait(false);
        return position > 0 ? new StateChangeCursor(position) : null;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);

        string file = CursorFile(outboxId);
        SemaphoreSlim gate = Gates.GetOrAdd(file, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long stored = await ReadPositionAsync(file, cancellationToken).ConfigureAwait(false);
            if (cursor.Position <= stored)
            {
                return;
            }

            string temporary = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                };
                await using (var stream = new FileStream(temporary, options))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new OutboxCursorFile(outboxId, cursor.Position),
                        Json,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                await MoveFileWithRetryAsync(temporary, file, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask<long> ReadPositionAsync(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return 0;
        }

        await using var stream = new FileStream(
            file,
            new FileStreamOptions { Access = FileAccess.Read, Mode = FileMode.Open, Share = FileShare.ReadWrite | FileShare.Delete });
        OutboxCursorFile? stored = await JsonSerializer
            .DeserializeAsync<OutboxCursorFile>(stream, Json, cancellationToken)
            .ConfigureAwait(false);
        return stored?.Position ?? 0;
    }

    /// <summary>
    /// Move a file with bounded retry to ride out transient Windows sharing violations (e.g. indexer or antivirus
    /// briefly holding the destination). Rethrows immediately on UnauthorizedAccessException (which never resolves
    /// by waiting) and on exhaustion of retries.
    /// </summary>
    private static async ValueTask MoveFileWithRetryAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
    {
        const int maxRetries = 50;
        const int retryDelayMs = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                File.Move(sourceFile, targetFile, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        File.Move(sourceFile, targetFile, overwrite: true);
    }

    private string CursorFile(string outboxId)
    {
        string hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(outboxId)))
            .ToLowerInvariant();
        return Path.Combine(_directory, hash + ".cursor");
    }
}

/// <summary>The on-disk shape of a filesystem cursor file: the outbox id it belongs to, and its position.</summary>
/// <param name="OutboxId">The outbox id, carried so an operator can tell the hashed files apart.</param>
/// <param name="Position">The stored <see cref="StateChangeCursor.Position"/>, written as an exact JSON integer.</param>
public sealed record OutboxCursorFile(string OutboxId, long Position);
