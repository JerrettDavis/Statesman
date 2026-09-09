using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Statesman.Outbox;

/// <summary>
/// Runs one <see cref="StateChangeDispatcher"/> cycle per <see cref="OutboxOptions.PollInterval"/>
/// tick, with bounded exponential backoff after a failure. The poll interval is the floor on
/// dispatch latency rather than the only trigger: when the store implements
/// <see cref="IStateChangeNotifier"/>, the worker also wakes on a pushed hint and dispatches without
/// waiting out the interval. A store with no notifier polls on the interval alone.
/// </summary>
/// <remarks>
/// A dispatch failure is logged and the loop continues on the next tick, mirroring
/// <c>StatesmanHostedService</c>. It is not swallowed: the cursor was not advanced, so the same
/// records are re-read and re-published next cycle. A store running without a lease is reported once
/// at start — the documented-degradation half of the capability signaling convention, which is also
/// how a missing notifier is treated.
/// </remarks>
public sealed class StatesmanOutboxHostedService : BackgroundService, IAsyncDisposable
{
    private const int MaxBackoffDoublings = 16;

    private readonly StateChangeDispatcher _dispatcher;
    private readonly OutboxOptions _options;
    private readonly ILogger<StatesmanOutboxHostedService> _logger;
    private readonly TimeProvider _timeProvider;

    // Capacity 1, DropWrite: a burst of hints becomes exactly one extra cycle, and the subscription
    // pump never blocks the store that raised them. TryWrite reports success even when the hint is
    // discarded, which is the intended semantics here -- there is nothing to do about a dropped
    // hint, because the next cycle reads the feed from the persisted cursor either way.
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    private CancellationTokenSource? _subscriptionStop;
    private Task? _subscription;
    private bool _sinkDisposed;

    // 0 until DisposeAsync claims it. Interlocked rather than a plain field because a container is
    // free to call DisposeAsync more than once, and two concurrent calls would otherwise both await
    // the pump and both dispose the sink.
    private int _disposed;

    /// <summary>Creates the worker around an already-constructed dispatcher.</summary>
    public StatesmanOutboxHostedService(
        StateChangeDispatcher dispatcher,
        OutboxOptions options,
        ILogger<StatesmanOutboxHostedService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_dispatcher.RunningWithoutLease)
        {
            _logger.LogWarning(
                "Statesman outbox {Outbox} is dispatching store {Store} without a lease: the store has no IStateLeaseProvider and RequireLease was turned off. A second dispatcher would race this one's cursor.",
                _dispatcher.OutboxId,
                _options.StoreName);
        }

        IStateChangeNotifier? notifier = _dispatcher.ChangeNotifier;
        if (notifier is not null)
        {
            _subscriptionStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _subscription = Task.Run(
                () => PumpNotificationsAsync(notifier, _subscriptionStop.Token),
                CancellationToken.None);
        }
        else
        {
            _logger.LogInformation(
                "Statesman outbox {Outbox} polls store {Store} every {PollInterval}: the store has no IStateChangeNotifier, so there is no push hint to shorten that latency.",
                _dispatcher.OutboxId,
                _options.StoreName,
                _options.PollInterval);
        }

        using var timer = new PeriodicTimer(_options.PollInterval, _timeProvider);
        int consecutiveFailures = 0;
        bool wakeArmed = notifier is not null;
        bool standby = false;
        Task<bool>? tick = null;
        Task<bool>? woken = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // PeriodicTimer supports exactly one outstanding WaitForNextTickAsync and its
                // ValueTask must be consumed, so the pending tick is carried across iterations: when
                // a hint wins the race, this same task keeps waiting rather than a second wait being
                // issued. The wake wait is carried the same way so an abandoned waiter never
                // accumulates.
                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();

                if (standby || !wakeArmed)
                {
                    // A worker that could not take the lease cannot publish, so waking it once per
                    // hint would add a lease round trip per write to a worker with nothing to do. It
                    // waits out the interval instead and re-arms as soon as a cycle returns anything
                    // other than LeaseUnavailable. Since Phase 10 the leader HOLDS its lease across
                    // cycles, so this rule no longer has aggregate lease traffic to bound: a leader
                    // renews on LeaseRenewInterval and a standby replica attempts one acquire per
                    // PollInterval, neither of which scales with write volume. What the rule still
                    // buys is that a standby replica does not attempt an acquire per hint. Takeover
                    // is one PollInterval after a graceful stop (which releases) and
                    // LeaseTtl + PollInterval after a crash (which does not).
                    if (!await tick.ConfigureAwait(false))
                    {
                        return;
                    }

                    tick = null;
                }
                else
                {
                    woken ??= _wake.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    Task completed = await Task.WhenAny(tick, woken).ConfigureAwait(false);
                    if (ReferenceEquals(completed, tick))
                    {
                        if (!await tick.ConfigureAwait(false))
                        {
                            return;
                        }

                        tick = null;
                    }
                    else
                    {
                        // False means the pump finished and completed the channel: stop racing it
                        // and poll on the interval alone from here on.
                        wakeArmed = await woken.ConfigureAwait(false);
                        woken = null;
                        if (!wakeArmed)
                        {
                            continue;
                        }

                        _wake.Reader.TryRead(out _);
                    }
                }

                try
                {
                    OutboxDispatchResult result = await _dispatcher.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    standby = result.Outcome == OutboxDispatchOutcome.LeaseUnavailable;

                    if (result.Outcome == OutboxDispatchOutcome.LeaseLost)
                    {
                        _logger.LogWarning(
                            "Statesman outbox {Outbox} lost its lease after publishing {Published} messages; the cursor was not advanced past anything unpublished.",
                            _dispatcher.OutboxId,
                            result.Published);
                    }

                    if (result.Skipped > 0)
                    {
                        _logger.LogError(
                            _dispatcher.LastSkippedError,
                            "Statesman outbox {Outbox} skipped {Skipped} messages the sink kept rejecting, per SkipPoisonAfterAttempts. Those messages were never delivered.",
                            _dispatcher.OutboxId,
                            result.Skipped);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;

                    // A thrown cycle is not an observation that someone else holds the lease, so it
                    // re-arms the wake path. The backoff below is what throttles a failing worker.
                    standby = false;
                    _logger.LogError(
                        exception,
                        "Statesman outbox {Outbox} dispatch failed ({Failures} consecutive failures). The cursor did not advance; the same records will be retried.",
                        _dispatcher.OutboxId,
                        consecutiveFailures);

                    // Before the delay, not after. OutboxOptions.MaxRetryDelay is validated below
                    // LeaseTtl with the message "so a stalled worker releases its lease rather than
                    // holding it while doing nothing", and that has to stay true now that the lease
                    // is held across cycles. The next cycle re-acquires.
                    await _dispatcher.ReleaseLeaseAsync().ConfigureAwait(false);

                    try
                    {
                        await Task.Delay(BackoffDelay(consecutiveFailures), _timeProvider, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            // Every exit path releases: the four returns inside the loop, the cancellation exit, and
            // a throw. IStateLease.DisposeAsync takes no cancellation token, so an already-cancelled
            // stopping token cannot skip this -- which is what lets a standby replica take over one
            // PollInterval after a graceful stop instead of waiting out LeaseTtl.
            await _dispatcher.ReleaseLeaseAsync().ConfigureAwait(false);
        }
    }

    private async Task PumpNotificationsAsync(IStateChangeNotifier notifier, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateChangeNotification _ in notifier.SubscribeAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalescing happens here, at the sink, not at the source: a burst becomes one
                // extra cycle. Nothing is read from the notification -- it is a hint, and the feed
                // is what the next cycle actually reads.
                _wake.Writer.TryWrite(0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The expected way this ends: the service is stopping or being disposed.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Statesman outbox {Outbox} lost its change-notification subscription to store {Store} and now polls every {PollInterval}. Notifications are a latency hint only, so no record is lost.",
                _dispatcher.OutboxId,
                _options.StoreName,
                _options.PollInterval);
        }
        finally
        {
            _wake.Writer.TryComplete();
        }
    }

    private TimeSpan BackoffDelay(int consecutiveFailures)
    {
        double scale = Math.Pow(2, Math.Min(consecutiveFailures - 1, MaxBackoffDoublings));
        double ticks = _options.MinRetryDelay.Ticks * scale;
        return ticks >= _options.MaxRetryDelay.Ticks
            ? _options.MaxRetryDelay
            : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Tears down the change-notification subscription, releases the dispatcher's lease, then
    /// disposes the dispatcher's sink. This service owns both for its lifetime, so a container
    /// disposing this service asynchronously is what releases them — nothing else in the shipped
    /// hosting path does, and a subscription left running holds a provider-side resource (on Redis,
    /// a server-side pub/sub subscription) for as long as the connection lives. Also runs the base
    /// <see cref="BackgroundService"/> disposal so the stopping-token cleanup still happens.
    /// </summary>
    /// <remarks>
    /// Runs once: a second call returns immediately, so a container that disposes twice does not
    /// dispose the sink twice. <c>StopAsync</c> cancels the pump through the stopping token its
    /// linked token follows, but only this method awaits the pump's teardown — and a host disposed
    /// synchronously runs <c>BackgroundService.Dispose()</c> and skips this method entirely, which
    /// is pre-existing and why the stopping-token cancellation has to be enough on its own.
    /// Awaiting the pump has no timeout by design: a notifier whose <c>SubscribeAsync</c> ignores
    /// the cancellation token it was given hangs disposal, and honouring that token is a provider
    /// obligation the capability's contract states rather than something this service can paper
    /// over. The lease release is idempotent and is also performed by <c>ExecuteAsync</c>'s own
    /// <c>finally</c>; this call covers a service disposed without ever having run.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_subscriptionStop is not null)
        {
            await _subscriptionStop.CancelAsync().ConfigureAwait(false);
        }

        if (_subscription is not null)
        {
            try
            {
                await _subscription.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pump already swallows its own cancellation; this covers a provider whose
                // enumerator throws before the pump's own handler can run.
            }

            _subscription = null;
        }

        _subscriptionStop?.Dispose();
        _subscriptionStop = null;

        // Idempotent, and needed for the case where ExecuteAsync never ran: the dispatcher is
        // constructed inside this service's factory and is never registered as a container service,
        // so nothing else will ever release its lease.
        await _dispatcher.ReleaseLeaseAsync().ConfigureAwait(false);

        if (!_sinkDisposed)
        {
            _sinkDisposed = true;
            await _dispatcher.Sink.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }
}
