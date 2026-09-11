using Statesman.Testing;

namespace Statesman.Tests;

/// <summary>
/// <see cref="ManualTimeProvider.CreateTimer"/>: the counts, the ordering, and the re-entrancy trap.
/// </summary>
/// <remarks>
/// Every assertion here is an exact count rather than a bound. A timer test that asserts "fired at
/// least once" passes against an implementation that fires on the system clock, which is precisely
/// the behaviour this override replaces.
/// </remarks>
public sealed class ManualTimeProviderTimerTests
{
    [Fact]
    public void A_timer_does_not_fire_before_its_due_time()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void A_one_shot_timer_fires_exactly_once_however_far_the_clock_moves()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, fired);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_periodic_timer_fires_once_per_period_inside_one_advance()
    {
        // The discriminating assertion, and the reason this is a count and not a bound: an
        // implementation that fires a periodic timer ONCE per Advance regardless of how far it
        // moved satisfies every "did it fire" assertion and fails this one.
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(
            _ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(3, fired);
    }

    [Fact]
    public void Timers_fire_in_due_time_order_and_the_clock_reads_the_firing_instant()
    {
        var clock = new ManualTimeProvider();
        DateTimeOffset started = clock.GetUtcNow();
        var order = new List<string>();
        var readings = new List<TimeSpan>();

        using ITimer late = clock.CreateTimer(
            _ => { order.Add("late"); readings.Add(clock.GetUtcNow() - started); },
            null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        using ITimer early = clock.CreateTimer(
            _ => { order.Add("early"); readings.Add(clock.GetUtcNow() - started); },
            null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["early", "late"], order);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], readings);
        Assert.Equal(started + TimeSpan.FromSeconds(5), clock.GetUtcNow());
    }

    [Fact]
    public void A_callback_that_advances_the_clock_does_not_deadlock()
    {
        // The re-entrancy trap. _gate is a plain monitor, so a callback invoked while it is held
        // deadlocks on any of Advance, SetUtcNow, GetUtcNow, Change or Dispose -- which is most of
        // what a timer callback in a test does. Callbacks must be invoked outside the lock.
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer inner = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        using ITimer outer = clock.CreateTimer(
            _ => clock.Advance(TimeSpan.FromSeconds(10)), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fired);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 11, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public void A_disposed_timer_never_fires_again()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);

        timer.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Change_reschedules_relative_to_the_current_virtual_time()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);

        // The original ten-second due time is gone, not merely postponed.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void An_infinite_due_time_never_fires_and_an_infinite_period_does_not_repeat()
    {
        var clock = new ManualTimeProvider();
        int never = 0;
        int once = 0;
        using ITimer disabled = clock.CreateTimer(
            _ => never++, null, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1));
        using ITimer single = clock.CreateTimer(
            _ => once++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, never);
        Assert.Equal(1, once);
    }

    [Fact]
    public void Moving_the_clock_backwards_fires_nothing()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.SetUtcNow(clock.GetUtcNow() - TimeSpan.FromHours(1));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void An_out_of_domain_interval_is_rejected_the_way_a_real_timer_rejects_it()
    {
        var clock = new ManualTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(-2)));
    }

    [Fact]
    public async Task A_PeriodicTimer_over_this_provider_ticks_when_the_clock_advances()
    {
        // The integration proof, and the reason this override matters at all: both shipped consumers
        // of a TimeProvider timer reach it through PeriodicTimer, never through CreateTimer
        // directly. One hour, so the inherited real-clock timer this replaces cannot make the wait
        // below succeed by accident.
        var clock = new ManualTimeProvider();
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        Task<bool> pending = timer.WaitForNextTickAsync().AsTask();

        clock.Advance(TimeSpan.FromHours(1));

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
