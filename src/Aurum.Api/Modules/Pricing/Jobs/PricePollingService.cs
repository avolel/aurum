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
    private readonly GoldApiIoOptions _goldApi = options.Value.GoldApiIo;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_goldApi.Enabled)
        {
            logger.LogWarning("Price polling disabled: no source is enabled.");
            return;
        }

        GuardPollBudget();

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

    /// <summary>
    /// Fails fast when the configured cadence cannot fit the configured budget. Starting anyway
    /// means quietly exhausting a monthly quota partway through the month, which is the exact
    /// failure this whole subsystem exists to prevent.
    /// </summary>
    private void GuardPollBudget()
    {
        // Worst case: the longest month. Under-estimating the days would under-estimate the spend.
        var pollsPerPeriod = TimeSpan.FromDays(31).TotalSeconds / _goldApi.PollInterval.TotalSeconds;
        if (pollsPerPeriod > _goldApi.MonthlyRequestLimit)
        {
            var minimum = TimeSpan.FromSeconds(TimeSpan.FromDays(31).TotalSeconds / _goldApi.MonthlyRequestLimit);
            throw new OptionsValidationException(
                nameof(GoldApiIoOptions),
                typeof(GoldApiIoOptions),
                [
                    $"PollInterval {_goldApi.PollInterval} implies ~{pollsPerPeriod:F0} requests per period " +
                    $"but MonthlyRequestLimit is {_goldApi.MonthlyRequestLimit}. " +
                    // The days component is load-bearing: `hh` is the hour *within* a day, so a
                    // minimum spanning days renders as its remainder and hands the operator a
                    // value that fails this same guard. Any limit below ~31 crosses that boundary.
                    $"Use an interval of at least {minimum:dd\\.hh\\:mm\\:ss}."
                ]);
        }
    }
}
