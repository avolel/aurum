using Aurum.Api.Modules.Pricing.Entities;
using Aurum.Api.Modules.Pricing.Sources;
using Aurum.Api.Shared;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Modules.Pricing.Jobs;

/// <summary>
/// Polls the configured price sources on a schedule and persists canonical ticks.
/// </summary>
/// <remarks>
/// Singleton by construction: Phase 1's delta engine keeps in-memory ring buffers and assumes
/// exactly one poller in the deployment. If the API is ever scaled out, this service must be
/// hosted separately rather than replicated.
/// </remarks>
public class PricePollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<PriceSourcesOptions> options,
    TimeProvider clock,
    ILogger<PricePollingService> logger) : BackgroundService
{
    private readonly PriceSourceOptions _goldApi =
        options.Value.RequireByCode(GoldApiIoSource.SourceCode);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_goldApi.Enabled)
        {
            logger.LogWarning("Price polling disabled: no source is enabled.");
            return;
        }

        // The cadence-vs-budget guard that used to run here now runs in
        // PriceSourcesOptionsValidator, so an overspending configuration fails the process at boot
        // instead of after the host has reported healthy — and covers every configured source
        // rather than this one.
        using var timer = new PeriodicTimer(_goldApi.PollInterval, clock);

        // Poll once immediately so a fresh container has a price before the first interval
        // elapses, then settle into the cadence.
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (QuotaExhaustedException ex)
            {
                // Not a fault. There is nothing to retry until the period rolls, so sleep
                // through it rather than spinning on the timer.
                var wait = ex.ResetsAt - clock.GetUtcNow();
                logger.LogError("{Source} out of quota; pausing polling for {Wait}.", ex.SourceCode, wait);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Phase 1 replaces this with the failover chain and a per-source circuit breaker.
                logger.LogError(ex, "Price poll failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IPriceSource>();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        var quote = await source.GetLatestQuoteAsync(PriceSymbols.Gold, ct);
        db.PriceTicks.Add(quote.ToTick());

        var registration = await db.PriceSources.FindAsync([source.Code], ct);
        if (registration is not null)
        {
            registration.LastSuccessAt = quote.ReceivedAt;
            registration.LastFailureReason = null;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Tick {Symbol} mid={Mid} from {Source}, {StalenessMs}ms stale.",
            quote.Symbol, quote.Mid, quote.SourceCode,
            (quote.ReceivedAt - quote.ObservedAt).TotalMilliseconds);
    }
}
