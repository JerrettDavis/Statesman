namespace Statesman.Tooling;

/// <summary>
/// Thrown when <see cref="StateLedgerRestore"/> refuses an export — wrong format, wrong root, wrong
/// declaration fingerprint, a truncated or corrupt file, or a target that already holds history under
/// the root. Every refusal happens before any record is imported. A target that lacks
/// <see cref="IStateLedgerReplica"/> or <see cref="IPartitionCatalog"/> throws
/// <see cref="NotSupportedException"/> instead.
/// </summary>
public sealed class StateLedgerRestoreException : InvalidOperationException
{
    public StateLedgerRestoreException(string message)
        : base(message)
    {
    }

    public StateLedgerRestoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
