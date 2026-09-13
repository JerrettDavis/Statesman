using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against the Entity Framework Core provider over a
/// database that can host two concurrent transactions.
/// </summary>
/// <remarks>
/// <see cref="EntityFrameworkTestConcurrency.ConcurrentTransactions"/> rather than the shared
/// single-connection fixture the lease suite uses: the in-flight test needs two concurrent
/// transactions, and one Microsoft.Data.Sqlite connection object cannot host them. On SQLite that
/// means a file, where the second writer blocks on <c>BEGIN IMMEDIATE</c> until the first commits,
/// which is the serialization point under test; on a server engine it is an ordinary database.
/// </remarks>
public sealed class EntityFrameworkChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync's only clock read is OccurredAt, inside the open serializable transaction and
    // after sequence.Value++ has allocated the position.
    protected override int PauseCallIndex => 1;

    // docs/providers/index.md: "Entity Framework Core's unique index on GlobalPosition rejects a
    // collision mid-import". StatesmanLedgerDbContext puts HasIndex(GlobalPosition).IsUnique() on
    // StatesmanRecords, so this provider genuinely cannot interleave two lineages at one position --
    // a documented provider difference rather than a defect, and the test below is where it is
    // asserted instead of silently skipped.
    protected override bool RejectsCollidingPositions => true;

    [Fact]
    public async Task Importing_a_second_address_at_an_occupied_position_throws()
    {
        await using ConformanceStore? store = await CreateAsync(TimeProvider.System);
        Assert.NotNull(store);
        Assert.True(store!.Store.TryGetCapability(out IStateLedgerReplica? replica));

        var addressA = new StateAddress("app", "conformance/collide-a", StatePartition.Default);
        var addressB = new StateAddress("app", "conformance/collide-b", StatePartition.Default);
        await replica!.ImportAsync(Colliding(addressA));

        await Assert.ThrowsAsync<DbUpdateException>(async () => await replica.ImportAsync(Colliding(addressB)));
    }

    private static StateRecord Colliding(StateAddress address) => new()
    {
        Address = address,
        Revision = 1,
        GlobalPosition = 100,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes("colliding"),
        Source = "test",
    };

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock) =>
        ConformanceProviders.EntityFrameworkAsync(clock, EntityFrameworkTestConcurrency.ConcurrentTransactions);
}
