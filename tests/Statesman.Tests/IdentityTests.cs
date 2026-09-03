namespace Statesman.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void Default_identity_values_are_canonical_and_safe()
    {
        StatePath path = default;
        StatePartition partition = default;
        var address = new StateAddress("Application", new StatePath("Users/Profile"), partition);

        Assert.Equal(StatePath.Root, path);
        Assert.True(path.IsRoot);
        Assert.Equal(string.Empty, path.Value);
        Assert.Equal(StatePartition.Default, partition);
        Assert.Equal("default", partition.Value);
        Assert.Equal("application::users/profile::default", address.Canonical);
    }

    [Fact]
    public void Address_validation_rejects_default_or_root_addresses()
    {
        Assert.Throws<ArgumentException>(() => default(StateAddress).Validate());
        Assert.Throws<ArgumentException>(() =>
            new StateAddress("root", StatePath.Root, StatePartition.Default).Validate());
    }
}
