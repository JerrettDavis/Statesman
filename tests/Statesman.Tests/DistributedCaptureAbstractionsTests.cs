namespace Statesman.Tests;

public sealed class DistributedCaptureAbstractionsTests
{
    [Fact]
    public void StateCaptureConsistency_declares_process_local_and_two_distributed_levels()
    {
        Assert.Equal(0, (int)StateCaptureConsistency.ProcessLocal);
        Assert.True(Enum.IsDefined(StateCaptureConsistency.ReadCommittedDistributed));
        Assert.True(Enum.IsDefined(StateCaptureConsistency.SnapshotDistributed));
    }

    [Fact]
    public void IDistributedCapture_is_a_state_capability()
    {
        Assert.True(typeof(IStateCapability).IsAssignableFrom(typeof(IDistributedCapture)));
    }
}
