namespace Statesman.Testing;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it, and whose timers fire
/// on that clock rather than on the system one.
/// </summary>
/// <remarks>
/// Before ROADMAP 0.3 Phase 12 this type did not override <see cref="CreateTimer"/>. It inherited
/// the base implementation, which schedules a real <c>System.Threading.Timer</c> — so a caller who
/// passed it to <c>new PeriodicTimer(interval, provider)</c> got a timer that silently ignored
/// <see cref="Advance"/>. Existing callers therefore see a behaviour change: their timers are now
/// virtual.
/// </remarks>
public sealed class ManualTimeProvider : TimeProvider
{
    // The domain System.Threading.Timer accepts: Timeout.InfiniteTimeSpan, or 0..4294967294 ms.
    // Matched exactly, so a test that passes a bad interval fails the same way it would against
    // TimeProvider.System.
    private const long MaxSupportedTimeoutMilliseconds = 0xFFFFFFFE;

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;

    /// <summary>Creates a clock.</summary>
    /// <param name="initial">The instant the clock starts at. Defaults to 2026-01-01T00:00:00Z.</param>
    public ManualTimeProvider(DateTimeOffset? initial = null)
    {
        _utcNow = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ticks of the virtual clock, so <see cref="TimeProvider.GetElapsedTime(long)"/> measures
    /// elapsed VIRTUAL time. Without this the base implementation returns
    /// <c>Stopwatch.GetTimestamp()</c>, and any caller that measures an interval with timestamps
    /// rather than with a timer silently ran on the system clock — the same class of surprise
    /// <see cref="CreateTimer"/> had before ROADMAP 0.3 Phase 12. Existing callers therefore see a
    /// behaviour change, from the system counter to this clock.
    /// </remarks>
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _utcNow.UtcTicks;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Required together with <see cref="GetTimestamp"/> and not optional beside it: the base
    /// <see cref="TimeProvider.GetElapsedTime(long, long)"/> scales a raw timestamp delta by
    /// <see cref="TimeSpan.TicksPerSecond"/> divided by this value. Left inherited, it would report
    /// <c>Stopwatch.Frequency</c> — the same 10,000,000 on Windows, and 1,000,000,000 on Linux, where
    /// every elapsed span this clock produced would read 100 times short.
    /// </remarks>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves the clock forward, firing every timer due in the interval crossed.</summary>
    /// <param name="duration">How far to move. Must not be negative.</param>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        DateTimeOffset target;
        lock (_gate)
        {
            target = _utcNow + duration;
        }

        MoveForwardTo(target);
    }

    /// <summary>Sets the clock, firing every timer due in the interval crossed if it moves forward.</summary>
    /// <param name="value">The instant to set the clock to.</param>
    public void SetUtcNow(DateTimeOffset value)
    {
        DateTimeOffset target = value.ToUniversalTime();
        lock (_gate)
        {
            if (target <= _utcNow)
            {
                // Moving the clock backwards fires nothing: a due time is an instant, and rewinding
                // past it does not make it due again.
                _utcNow = target;
                return;
            }
        }

        MoveForwardTo(target);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned timer fires only when <see cref="Advance"/> or <see cref="SetUtcNow"/> crosses
    /// its due time, in due-time order, once per elapsed period. A <paramref name="period"/> of
    /// <see cref="TimeSpan.Zero"/> is treated as one-shot: a zero period would make one
    /// <see cref="Advance"/> fire forever, which is a hang rather than a test result.
    /// <c>PeriodicTimer</c> requires a positive period and never reaches that case.
    /// </remarks>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateInterval(dueTime, nameof(dueTime));
        ValidateInterval(period, nameof(period));
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state, _utcNow, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    private static void ValidateInterval(TimeSpan value, string name)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value < TimeSpan.Zero || value.TotalMilliseconds > MaxSupportedTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                "A timer interval must be Timeout.InfiniteTimeSpan or between zero and 4294967294 milliseconds.");
        }
    }

    private void MoveForwardTo(DateTimeOffset target)
    {
        while (true)
        {
            ManualTimer? due = null;
            DateTimeOffset dueAt = default;
            lock (_gate)
            {
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.DueAt is DateTimeOffset at && at <= target && (due is null || at < dueAt))
                    {
                        due = timer;
                        dueAt = at;
                    }
                }

                if (due is null)
                {
                    if (target > _utcNow)
                    {
                        _utcNow = target;
                    }

                    return;
                }

                // The clock reads the FIRING instant while the callback runs, not the instant the
                // caller asked for, so a callback that reads GetUtcNow sees when it fired.
                if (dueAt > _utcNow)
                {
                    _utcNow = dueAt;
                }

                // Rescheduling under the lock is what bounds this loop: every firing moves that
                // timer's next due time forward by a strictly positive period, so a finite timer
                // list can only be due finitely often below a fixed target.
                due.ScheduleNextUnsafe(dueAt);
            }

            // Outside the lock, always. _gate is a plain monitor, so firing a callback while holding
            // it deadlocks any callback that calls Advance, SetUtcNow, GetUtcNow, Change or Dispose
            // -- which is most of what a timer callback in a test does. Re-entrancy is fine: a
            // callback that calls Advance runs a nested MoveForwardTo, and this loop then finds
            // nothing further due at or below its own target and returns without rewinding.
            due.Invoke();
        }
    }

    private void RemoveUnsafe(ManualTimer timer) => _timers.Remove(timer);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider provider,
            TimerCallback callback,
            object? state,
            DateTimeOffset now,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
        }

        // Read and written only under the provider's gate.
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateInterval(dueTime, nameof(dueTime));
            ValidateInterval(period, nameof(period));
            lock (_provider._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _provider._utcNow + dueTime;
                return true;
            }
        }

        // The caller must already hold the provider's gate.
        public void ScheduleNextUnsafe(DateTimeOffset firedAt) =>
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                ? null
                : firedAt + _period;

        public void Invoke() => _callback(_state);

        public void Dispose()
        {
            lock (_provider._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DueAt = null;
                _provider.RemoveUnsafe(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
