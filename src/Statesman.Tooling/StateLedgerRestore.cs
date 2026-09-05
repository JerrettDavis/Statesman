using System.Text;
using System.Text.Json;

namespace Statesman.Tooling;

/// <summary>Options for <see cref="StateLedgerRestore.RestoreAsync"/>.</summary>
public sealed record StateLedgerRestoreOptions
{
    /// <summary>
    /// When <see langword="false"/> (the default), restore refuses a target whose
    /// <see cref="IPartitionCatalog"/> already lists any partition under the export's root, because
    /// <c>GlobalPosition</c> values from independent histories collide — the Entity Framework Core
    /// provider's unique index would fail mid-import, other providers would silently interleave two
    /// histories. Set <see langword="true"/> to re-run a restore onto the same target after a
    /// store-side failure mid-import (every import is exact and idempotent, so re-running is the
    /// recovery path) or to restore over a store known to share the export's position lineage.
    /// </summary>
    public bool AllowNonEmptyTarget { get; init; }
}

/// <summary>What a restore imported.</summary>
public sealed record StateLedgerRestoreSummary
{
    public required string Root { get; init; }

    public required string Fingerprint { get; init; }

    /// <summary>Records imported.</summary>
    public required long Records { get; init; }

    /// <summary>Distinct partitions those records belong to.</summary>
    public required int Partitions { get; init; }

    /// <summary>The export header's timestamp.</summary>
    public required DateTimeOffset ExportedAt { get; init; }
}

/// <summary>
/// Restores an export written by <see cref="StateLedgerExport"/> into a store, exactly — same revisions,
/// same <c>GlobalPosition</c>s, same timestamps — through <see cref="IStateLedgerReplica.ImportAsync"/>.
/// </summary>
/// <remarks>
/// Refusal is total, never partial. The whole export is read and validated first — header format,
/// root and fingerprint against <c>manifest</c>, every record through
/// <see cref="StateRecord.Validate"/>, and the trailer's record count — without contacting the target.
/// Only then is the target checked for <see cref="IStateLedgerReplica"/> (<see cref="NotSupportedException"/>
/// if missing; restore into a tiered store's cold store directly) and, unless
/// <see cref="StateLedgerRestoreOptions.AllowNonEmptyTarget"/> is set, for existing partitions under the
/// root. Import then proceeds in file order. A store failure mid-import leaves the records imported so
/// far in place; re-run with <see cref="StateLedgerRestoreOptions.AllowNonEmptyTarget"/> to finish.
/// Payloads are never deserialized, so declared schema migrations still apply at read time exactly as
/// they would on the original store. The whole export is held in memory for the duration of the call.
/// </remarks>
public static class StateLedgerRestore
{
    /// <summary>Validates and imports the export in <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static async ValueTask<StateLedgerRestoreSummary> RestoreAsync(
        IStateLedgerStore target,
        StatesmanManifest manifest,
        Stream source,
        StateLedgerRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        StateLedgerRestoreOptions restoreOptions = options ?? new StateLedgerRestoreOptions();

        StateLedgerExportHeader header;
        List<StateRecord> records;
        using (var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            (header, records) = await ReadAndValidateAsync(reader, manifest, cancellationToken).ConfigureAwait(false);
        }

        if (!target.TryGetCapability(out IStateLedgerReplica? replica))
        {
            throw new NotSupportedException(
                $"Store '{target.Name}' does not implement IStateLedgerReplica, which restore requires to import exact records. " +
                "When the target is a tiered store, restore into its authoritative (cold) store directly.");
        }

        if (!restoreOptions.AllowNonEmptyTarget)
        {
            await EnsureRootIsEmptyAsync(target, header.Root, cancellationToken).ConfigureAwait(false);
        }

        foreach (StateRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await replica.ImportAsync(record, cancellationToken).ConfigureAwait(false);
        }

        return new StateLedgerRestoreSummary
        {
            Root = header.Root,
            Fingerprint = header.Fingerprint,
            Records = records.Count,
            Partitions = records.Select(record => record.Address.Canonical).Distinct(StringComparer.Ordinal).Count(),
            ExportedAt = header.ExportedAt,
        };
    }

    /// <summary>Validates and imports the export file at <paramref name="path"/> into <paramref name="target"/>.</summary>
    public static async ValueTask<StateLedgerRestoreSummary> RestoreFromFileAsync(
        IStateLedgerStore target,
        StatesmanManifest manifest,
        string path,
        StateLedgerRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            return await RestoreAsync(target, manifest, stream, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(StateLedgerExportHeader Header, List<StateRecord> Records)> ReadAndValidateAsync(
        StreamReader reader,
        StatesmanManifest manifest,
        CancellationToken cancellationToken)
    {
        int lineNumber = 0;
        string? line = await NextLineAsync(reader, cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new StateLedgerRestoreException("The export is empty: it contains no header line.");
        }

        lineNumber++;
        StateLedgerExportHeader header = Parse<StateLedgerExportHeader>(line, lineNumber, "header");
        ValidateHeader(header, manifest);

        var records = new List<StateRecord>();
        string? pending = await NextLineAsync(reader, cancellationToken).ConfigureAwait(false);
        lineNumber++;
        if (pending is null)
        {
            throw new StateLedgerRestoreException("The export is truncated: it ends after the header with no trailer line.");
        }

        while (true)
        {
            string? next = await NextLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            records.Add(ParseRecord(pending, lineNumber));
            pending = next;
            lineNumber++;
        }

        StateLedgerExportTrailer trailer;
        try
        {
            trailer = JsonSerializer.Deserialize<StateLedgerExportTrailer>(pending, StateLedgerExportFormat.Json)
                ?? throw new JsonException("The trailer line deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new StateLedgerRestoreException(
                $"The export is truncated: line {lineNumber} should be the trailer but is not one ({exception.Message}).", exception);
        }

        if (trailer.Records != records.Count)
        {
            throw new StateLedgerRestoreException(
                $"The export declares {trailer.Records} records but contains {records.Count}; it is truncated or corrupt.");
        }

        return (header, records);
    }

    private static void ValidateHeader(StateLedgerExportHeader header, StatesmanManifest manifest)
    {
        if (!string.Equals(header.Format, StateLedgerExportFormat.Version, StringComparison.Ordinal))
        {
            throw new StateLedgerRestoreException(
                $"Unsupported export format '{header.Format}'; this version restores '{StateLedgerExportFormat.Version}'.");
        }

        if (!string.Equals(header.Root, manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new StateLedgerRestoreException(
                $"Export root '{header.Root}' cannot restore into root '{manifest.Id}'.");
        }

        if (!string.Equals(header.Fingerprint, manifest.Fingerprint, StringComparison.Ordinal))
        {
            throw new StateLedgerRestoreException(
                $"Export fingerprint '{header.Fingerprint}' does not match the target declaration fingerprint '{manifest.Fingerprint}'; " +
                "restore refuses to import history recorded under a different declaration.");
        }
    }

    private static StateRecord ParseRecord(string line, int lineNumber)
    {
        StateLedgerExportRecord exported = Parse<StateLedgerExportRecord>(line, lineNumber, "ledger record");
        StateRecord record = exported.ToRecord();
        try
        {
            record.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new StateLedgerRestoreException(
                $"Line {lineNumber} of the export is not a valid ledger record: {exception.Message}", exception);
        }

        return record;
    }

    private static T Parse<T>(string line, int lineNumber, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, StateLedgerExportFormat.Json)
                ?? throw new JsonException($"The {what} line deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new StateLedgerRestoreException(
                $"Line {lineNumber} of the export is not a Statesman ledger export {what}: {exception.Message}", exception);
        }
    }

    private static async Task<string?> NextLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || !string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }
    }

    private static async Task EnsureRootIsEmptyAsync(IStateLedgerStore target, string root, CancellationToken cancellationToken)
    {
        if (!target.TryGetCapability(out IPartitionCatalog? catalog))
        {
            throw new NotSupportedException(
                $"Store '{target.Name}' does not implement IPartitionCatalog, which restore requires to verify the target holds no history under root '{root}'. " +
                "Set StateLedgerRestoreOptions.AllowNonEmptyTarget to skip that check.");
        }

        await foreach (StatePartitionDescriptor descriptor in catalog.ListPartitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(descriptor.Address.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new StateLedgerRestoreException(
                    $"Store '{target.Name}' already holds partition '{descriptor.Address.Canonical}' under root '{root}'; " +
                    "restore refuses to interleave exported positions with existing history. " +
                    "Set StateLedgerRestoreOptions.AllowNonEmptyTarget to restore anyway.");
            }
        }
    }
}
