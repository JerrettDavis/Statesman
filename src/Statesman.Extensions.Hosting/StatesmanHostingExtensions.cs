using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Statesman;

public sealed class StatesmanHostedServiceOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(15);

    public bool StopHostOnInitializationFailure { get; set; } = true;
}

public static class StatesmanHostingExtensions
{
    public static IServiceCollection AddStatesmanHosting(
        this IServiceCollection services,
        Action<StatesmanHostedServiceOptions>? configure = null)
    {
        var options = new StatesmanHostedServiceOptions();
        configure?.Invoke(options);

        if (options.ScanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "The Statesman scan interval must be greater than zero.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StatesmanHostedService>());
        return services;
    }
}

internal sealed class StatesmanHostedService : BackgroundService
{
    private readonly IReadOnlyList<IStatesman> _roots;
    private readonly StatesmanHostedServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StatesmanHostedService> _logger;

    public StatesmanHostedService(
        IEnumerable<IStatesman> roots,
        StatesmanHostedServiceOptions options,
        TimeProvider timeProvider,
        ILogger<StatesmanHostedService> logger)
    {
        _roots = roots.ToArray();
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IStatesman root in _roots)
        {
            try
            {
                await root.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!_options.StopHostOnInitializationFailure)
            {
                _logger.LogError(exception, "Statesman root {Root} failed to initialize.", root.Id);
            }
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ScanInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (IStatesman root in _roots)
            {
                try
                {
                    await root.MaintainAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Statesman maintenance failed for root {Root}.", root.Id);
                }
            }
        }
    }
}
