using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Statesman.Outbox;

/// <summary>
/// Runs one <see cref="StateChangeDispatcher"/> cycle per <see cref="OutboxOptions.PollInterval"/>
/// tick, with bounded exponential backoff after a failure. The change feed has no push notification,
/// so the outbox polls.
/// </summary>
/// <remarks>
/// A dispatch failure is logged and the loop continues on the next tick, mirroring
/// <c>StatesmanHostedService</c>. It is not swallowed: the cursor was not advanced, so the same
/// records are re-read and re-published next cycle. A store running without a lease is reported once
/// at start — the documented-degradation half of the capability signaling convention.
/// </remarks>
public sealed class StatesmanOutboxHostedService : BackgroundService, IAsyncDisposable
{
    private const int MaxBackoffDoublings = 16;

    private readonly StateChangeDispatcher _dispatcher;
    private readonly OutboxOptions _options;
    private readonly ILogger<StatesmanOutboxHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private bool _sinkDisposed;

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

        using var timer = new PeriodicTimer(_options.PollInterval, _timeProvider);
        int consecutiveFailures = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                OutboxDispatchResult result = await _dispatcher.DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                consecutiveFailures = 0;

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
                _logger.LogError(
                    exception,
                    "Statesman outbox {Outbox} dispatch failed ({Failures} consecutive failures). The cursor did not advance; the same records will be retried.",
                    _dispatcher.OutboxId,
                    consecutiveFailures);

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

    private TimeSpan BackoffDelay(int consecutiveFailures)
    {
        double scale = Math.Pow(2, Math.Min(consecutiveFailures - 1, MaxBackoffDoublings));
        double ticks = _options.MinRetryDelay.Ticks * scale;
        return ticks >= _options.MaxRetryDelay.Ticks
            ? _options.MaxRetryDelay
            : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Disposes the dispatcher's sink. This service owns the sink for its lifetime, so a container
    /// disposing this service asynchronously is what flushes and releases it — nothing else in the
    /// shipped hosting path does. Also runs the base <see cref="BackgroundService"/> disposal so the
    /// stopping-token cleanup still happens.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_sinkDisposed)
        {
            _sinkDisposed = true;
            await _dispatcher.Sink.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }
}
