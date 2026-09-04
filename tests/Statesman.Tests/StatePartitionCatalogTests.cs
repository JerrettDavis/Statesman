namespace Statesman.Tests;

public sealed class StatePartitionCatalogTests
{
    [Fact]
    public void StatePartitionDescriptor_carries_an_address_and_a_last_position()
    {
        var address = new StateAddress("app", "feed/item", StatePartition.Default);
        var position = new StateChangeCursor(7);

        var descriptor = new StatePartitionDescriptor { Address = address, LastPosition = position };

        Assert.Equal(address, descriptor.Address);
        Assert.Equal(position, descriptor.LastPosition);
    }
}
