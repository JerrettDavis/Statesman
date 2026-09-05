using System.Text;
using System.Text.Json;

namespace Statesman.Tooling;

/// <summary>Options for <see cref="StateLedgerExport.ExportAsync"/>.</summary>
public sealed record StateLedgerExportOptions
{
    /// <summary>Free-form metadata written into the export header (environment, operator, ticket, ...).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clock that stamps <see cref="StateLedgerExportHeader.ExportedAt"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>What an export wrote.</summary>
public sealed record StateLedgerExportSummary
{
    public required string Root { get; init; }

    public required string Fingerprint { get; init; }

    /// <summary>Records written — every retained revision of every exported partition.</summary>
    public required long Records { get; init; }

    /// <summary>Distinct partitions exported.</summary>
    public required int Partitions { get; init; }
}

/// <summary>
/// Exports one root's retained ledger history from a store to the newline-delimited JSON format
/// described by <see cref="StateLedgerExportFormat"/>, for <see cref="StateLedgerRestore"/> to import
/// exactly elsewhere.
/// </summary>
/// <remarks>
/// The export contains every revision the source store retains (post-retention history, exactly what
/// <see cref="IStateLedgerStore.ReadHistoryAsync"/> returns) for every partition the store's
/// <see cref="IPartitionCatalog"/> lists under <see cref="StatesmanManifest.Id"/> when enumeration begins,
/// partitions ordered by <see cref="StateAddress.Canonical"/> and revisions ascending within each. It is
/// <b>not</b> a cross-partition point-in-time snapshot: a write that lands while the export runs may or
/// may not appear, per partition. Quiesce writers for a consistent backup. Records under other roots
/// sharing the same store are excluded. The change feed is deliberately not used, so its at-least-once
/// and prune caveats do not apply. A source without <see cref="IPartitionCatalog"/> throws
/// <see cref="NotSupportedException"/>.
/// </remarks>
public static class StateLedgerExport
{
    private static readonly StateHistoryOptions FullHistoryOldestFirst = new() { Take = null, NewestFirst = false };

    /// <summary>Streams an export of <paramref name="source"/> for <paramref name="manifest"/>'s root into <paramref name="destination"/>.</summary>
    public static async ValueTask<StateLedgerExportSummary> ExportAsync(
        IStateLedgerStore source,
        StatesmanManifest manifest,
        Stream destination,
        StateLedgerExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The export destination stream must be writable.", nameof(destination));
        }

        StateLedgerExportOptions exportOptions = options ?? new StateLedgerExportOptions();
        if (!source.TryGetCapability(out IPartitionCatalog? catalog))
        {
            throw new NotSupportedException(
                $"Store '{source.Name}' does not implement IPartitionCatalog, which export requires to enumerate partitions.");
        }

        var partitions = new List<StateAddress>();
        await foreach (StatePartitionDescriptor descriptor in catalog.ListPartitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(descriptor.Address.Root, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                partitions.Add(descriptor.Address);
            }
        }

        partitions.Sort(static (left, right) => string.CompareOrdinal(left.Canonical, right.Canonical));

        var header = new StateLedgerExportHeader
        {
            Format = StateLedgerExportFormat.Version,
            Root = manifest.Id,
            Fingerprint = manifest.Fingerprint,
            Store = source.Name,
            ExportedAt = exportOptions.TimeProvider.GetUtcNow(),
            Metadata = new Dictionary<string, string>(exportOptions.Metadata, StringComparer.OrdinalIgnoreCase),
        };

        long records = 0;
        var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
        {
            NewLine = "\n",
        };
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(header, StateLedgerExportFormat.Json)).ConfigureAwait(false);

            foreach (StateAddress address in partitions)
            {
                await foreach (StateRecord record in source.ReadHistoryAsync(address, FullHistoryOldestFirst, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string line = JsonSerializer.Serialize(StateLedgerExportRecord.FromRecord(record), StateLedgerExportFormat.Json);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    records++;
                }
            }

            var trailer = new StateLedgerExportTrailer { Records = records, Partitions = partitions.Count };
            await writer.WriteLineAsync(JsonSerializer.Serialize(trailer, StateLedgerExportFormat.Json)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new StateLedgerExportSummary
        {
            Root = manifest.Id,
            Fingerprint = manifest.Fingerprint,
            Records = records,
            Partitions = partitions.Count,
        };
    }

    /// <summary>Exports to a file, creating the containing directory and replacing any existing file.</summary>
    public static async ValueTask<StateLedgerExportSummary> ExportToFileAsync(
        IStateLedgerStore source,
        StatesmanManifest manifest,
        string path,
        StateLedgerExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            return await ExportAsync(source, manifest, stream, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
