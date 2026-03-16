using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Fronting;

namespace Octocon.Infrastructure.Coordination;

/// <summary>
/// Primary-node background service that batches fronting-state-change signals and flushes
/// them every 5 seconds, mirroring <c>Octocon.Global.FrontNotifier</c> from the legacy
/// Elixir runtime.
/// <para>
/// Subscribes to <see cref="FrontingStateChangedEvent"/> via <see cref="IClusterEventBus"/>.
/// Phase N will inject <c>IFrontingRepository</c> and <c>IFCMService</c> to read current
/// fronting alters and push notifications to friends.
/// </para>
/// </summary>
public sealed class FrontNotifierBackgroundService(
    IClusterEventBus eventBus,
    ILogger<FrontNotifierBackgroundService> logger)
    : BackgroundService
{
    // system_id → last-change timestamp (ms since epoch)
    private readonly ConcurrentDictionary<string, long> _pending = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FrontNotifierBackgroundService starting (primary node).");

        var subscription = ConsumeEventsAsync(stoppingToken);
        var flush = FlushLoopAsync(stoppingToken);

        await Task.WhenAll(subscription, flush).ConfigureAwait(false);

        logger.LogInformation("FrontNotifierBackgroundService stopped.");
    }

    private async Task ConsumeEventsAsync(CancellationToken ct)
    {
        await foreach (var evt in eventBus.SubscribeAsync<FrontingStateChangedEvent>(ct).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pending.AddOrUpdate(evt.SystemId, now, (_, _) => now);
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000;

            foreach (var systemId in _pending.Keys)
            {
                if (_pending.TryGetValue(systemId, out var ts) && ts < cutoff &&
                    _pending.TryRemove(systemId, out _))
                {
                    // TODO Phase N: query IFrontingRepository for current alters, then call IFCMService.
                    logger.LogDebug(
                        "FrontNotifier flush: system={SystemId}", systemId);
                }
            }
        }
    }
}
