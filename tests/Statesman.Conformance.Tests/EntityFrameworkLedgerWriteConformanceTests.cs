using Statesman.TestHelpers;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared conditional-write suite, run against the Entity Framework Core provider over a database
/// that can host concurrent transactions.
/// </summary>
/// <remarks>
/// <see cref="EntityFrameworkTestConcurrency.ConcurrentTransactions"/> rather than the shared
/// single-connection fixture: <see cref="LedgerWriteConformanceTests.Exactly_one_of_many_racing_absent_appends_wins"/>
/// opens eight concurrent transactions, and one Microsoft.Data.Sqlite connection object cannot host
/// them.
/// </remarks>
public sealed class EntityFrameworkLedgerWriteConformanceTests : LedgerWriteConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System, EntityFrameworkTestConcurrency.ConcurrentTransactions);
}
