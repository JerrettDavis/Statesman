using System.Text;
using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

/// <summary>
/// Pins SQL Server's 900-byte clustered index key limit against the shipped key shape, in both
/// directions.
/// </summary>
/// <remarks>
/// <para>
/// <c>StatesmanHeads</c>' primary key is <c>Root</c> (128) + <c>Path</c> (512) + <c>Partition</c>
/// (256) <c>nvarchar</c>, 1792 bytes at the maximum, and <c>StatesmanRecords</c> adds
/// <c>Revision</c> for 1800. SQL Server accepts both at <c>CREATE TABLE</c> with a warning and
/// enforces the limit at INSERT (<c>Msg 1946</c>), so <c>EnsureCreated</c> works and every realistic
/// address stores. That is why ROADMAP 0.3 Phase 12 documents the limit rather than changing the key
/// shape: the two Entity Framework Core packages ship no migrations, so shortening a column would
/// break every consumer's schema for a warning nobody has hit.
/// </para>
/// <para>
/// These two tests are what stops that ruling from silently rotting. If a future model change moves
/// the boundary, one of them fails.
/// </para>
/// </remarks>
public sealed class EntityFrameworkSqlServerKeyLimitTests
{
    // The address is "app" (3 characters) + the path + "default" (7), two bytes per character.
    private const int UnderLimitPathLength = 400; // (3 + 400 + 7) * 2 = 820 bytes
    private const int OverLimitPathLength = 460;  // (3 + 460 + 7) * 2 = 940 bytes

    [Fact]
    public async Task An_address_whose_key_fits_in_900_bytes_appends()
    {
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.SqlServer,
            EntityFrameworkTestDatabase.SqlServerSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<KeyLimitContext> factory =
            await database.CreateFactoryAsync<KeyLimitContext>(options => new KeyLimitContext(options));
        await using var store = new EntityFrameworkStateLedgerStore<KeyLimitContext>("database", factory);

        StateAppendResult result = await store.AppendAsync(
            Address(UnderLimitPathLength), StateWriteCondition.Absent, Commit("under"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_address_whose_key_exceeds_900_bytes_fails_with_a_clear_provider_error()
    {
        // The write condition is Absent and no head exists, so AppendAsync's catch block re-reads,
        // finds the condition still matching, and rethrows -- which is the documented "preserve the
        // provider failure for the caller" path, not a Conflict. An operator therefore sees the
        // engine's own message rather than a silent no-op.
        Assert.SkipUnless(
            EntityFrameworkTestDatabase.SelectedEngine == EntityFrameworkTestEngine.SqlServer,
            EntityFrameworkTestDatabase.SqlServerSkipReason);

        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<KeyLimitContext> factory =
            await database.CreateFactoryAsync<KeyLimitContext>(options => new KeyLimitContext(options));
        await using var store = new EntityFrameworkStateLedgerStore<KeyLimitContext>("database", factory);

        DbUpdateException thrown = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.AppendAsync(Address(OverLimitPathLength), StateWriteCondition.Absent, Commit("over")));

        Assert.Contains("900", thrown.ToString(), StringComparison.Ordinal);
    }

    private static StateAddress Address(int pathLength) =>
        new("app", new StatePath(new string('p', pathLength)), StatePartition.Default);

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed class KeyLimitContext : StatesmanLedgerDbContext
    {
        public KeyLimitContext(DbContextOptions<KeyLimitContext> options)
            : base(options)
        {
        }
    }
}
