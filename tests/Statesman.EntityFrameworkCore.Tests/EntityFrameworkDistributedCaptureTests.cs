using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;

namespace Statesman.EntityFrameworkCore.Tests;

public sealed class EntityFrameworkDistributedCaptureTests
{
    [Fact]
    public void EntityFrameworkStateLedgerStore_reports_distributed_capture_capability()
    {
        Assert.True(typeof(EntityFrameworkStateLedgerStore<>).GetInterfaces().Contains(typeof(IDistributedCapture)));
    }

    [Theory]
    [InlineData(StateCaptureConsistency.ReadCommittedDistributed)]
    [InlineData(StateCaptureConsistency.SnapshotDistributed)]
    public async Task CaptureAsync_returns_the_current_record_per_address_including_absent_ones(
        StateCaptureConsistency consistency)
    {
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestCaptureContext> factory =
            await database.CreateFactoryAsync<TestCaptureContext>(options => new TestCaptureContext(options));

        var store = new EntityFrameworkStateLedgerStore<TestCaptureContext>("database", factory);
        var addressA = new StateAddress("app", "capture/a", StatePartition.Default);
        var addressB = new StateAddress("app", "capture/b", StatePartition.Default);
        var addressC = new StateAddress("app", "capture/absent", StatePartition.Default);
        StateAppendResult a = await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
        StateAppendResult b = await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));

        IReadOnlyDictionary<StateAddress, StateRecord?> captured = await store.CaptureAsync(
            new[] { addressA, addressB, addressC }, consistency);

        Assert.Equal(3, captured.Count);
        Assert.Equal(a.Record!.GlobalPosition, captured[addressA]!.GlobalPosition);
        Assert.Equal(b.Record!.GlobalPosition, captured[addressB]!.GlobalPosition);
        Assert.Null(captured[addressC]);
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed class TestCaptureContext : StatesmanLedgerDbContext
    {
        public TestCaptureContext(DbContextOptions<TestCaptureContext> options)
            : base(options)
        {
        }
    }
}
