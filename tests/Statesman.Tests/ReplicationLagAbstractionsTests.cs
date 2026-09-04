namespace Statesman.Tests;

public sealed class ReplicationLagAbstractionsTests
{
    [Fact]
    public void IReplicationLagSource_is_a_state_capability()
    {
        Assert.True(typeof(IStateCapability).IsAssignableFrom(typeof(IReplicationLagSource)));
    }

    [Fact]
    public void PositionGap_is_the_distance_the_replica_is_behind_the_authority()
    {
        var lag = new StateReplicationLag
        {
            AuthoritativePosition = 10,
            ReplicaPosition = 7,
            PartitionsBehind = 2,
        };

        Assert.Equal(3, lag.PositionGap);
        Assert.False(lag.IsCaughtUp);
    }

    [Fact]
    public void PositionGap_clamps_to_zero_when_the_replica_is_ahead()
    {
        var lag = new StateReplicationLag
        {
            AuthoritativePosition = 5,
            ReplicaPosition = 9,
            PartitionsBehind = 0,
        };

        Assert.Equal(0, lag.PositionGap);
        Assert.True(lag.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_requires_no_partitions_behind_even_when_positions_match()
    {
        var lag = new StateReplicationLag
        {
            AuthoritativePosition = 5,
            ReplicaPosition = 5,
            PartitionsBehind = 1,
        };

        Assert.Equal(0, lag.PositionGap);
        Assert.False(lag.IsCaughtUp);
    }

    [Fact]
    public void IsCaughtUp_is_true_for_two_empty_tiers()
    {
        var lag = new StateReplicationLag
        {
            AuthoritativePosition = 0,
            ReplicaPosition = 0,
            PartitionsBehind = 0,
        };

        Assert.True(lag.IsCaughtUp);
    }
}
