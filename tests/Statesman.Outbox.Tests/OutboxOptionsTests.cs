namespace Statesman.Outbox.Tests;

public sealed class OutboxOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_require_a_lease()
    {
        var options = new OutboxOptions { StoreName = "primary" };

        options.Validate();

        Assert.True(options.RequireLease);
        Assert.Equal("default", options.OutboxId);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal("application/json", options.PayloadContentType);
        Assert.Null(options.SkipPoisonAfterAttempts);
    }

    [Fact]
    public void A_non_positive_batch_size_is_rejected()
    {
        var options = new OutboxOptions { StoreName = "primary", BatchSize = 0 };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Contains(nameof(OutboxOptions.BatchSize), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_max_retry_delay_at_or_above_the_lease_ttl_is_rejected()
    {
        var options = new OutboxOptions
        {
            StoreName = "primary",
            LeaseTtl = TimeSpan.FromSeconds(30),
            MaxRetryDelay = TimeSpan.FromSeconds(30),
        };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Contains(nameof(OutboxOptions.MaxRetryDelay), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_max_retry_delay_below_the_min_retry_delay_is_rejected()
    {
        var options = new OutboxOptions
        {
            StoreName = "primary",
            MinRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromSeconds(2),
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void The_effective_lease_renewal_interval_defaults_to_a_third_of_the_ttl()
    {
        var options = new OutboxOptions { StoreName = "primary", LeaseTtl = TimeSpan.FromSeconds(30) };

        options.Validate();

        Assert.Null(options.LeaseRenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.EffectiveLeaseRenewInterval);
    }

    [Fact]
    public void A_lease_renewal_interval_at_or_above_the_ttl_is_rejected()
    {
        var options = new OutboxOptions
        {
            StoreName = "primary",
            LeaseTtl = TimeSpan.FromSeconds(30),
            LeaseRenewInterval = TimeSpan.FromSeconds(30),
        };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Contains(nameof(OutboxOptions.LeaseRenewInterval), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_store_name_is_rejected()
    {
        var options = new OutboxOptions { StoreName = "  " };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
