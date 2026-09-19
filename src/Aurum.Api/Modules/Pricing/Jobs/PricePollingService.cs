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
    IOptions<PricePollingOptions> polling,
    ILogger<PricePollingService> logger) : BackgroundService
{
    /// <summary>
    /// Enabled sources in failover order, so the head is the primary. Ordering here mirrors
    /// <c>PollOnceAsync</c>'s ordering of <c>IEnumerable&lt;IPriceSource&gt;</c>, which is what
    /// makes the coverage log below describe the chain that actually runs.
    /// </summary>
    private readonly List<PriceSourceOptions> _enabled = options.Value.Sources.Values
        .Where(source => source.Enabled)
        .OrderBy(source => source.Priority)
        .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PriceSourcesOptionsValidator refuses a configuration with no enabled source, so this is
        // a backstop for a host that skipped validation rather than an expected state. Checking
        // the whole chain, not GoldAPI specifically: disabling the top provider promotes the next.
        if (_enabled.Count == 0)
        {
            logger.LogWarning("Price polling disabled: no source is enabled.");
            return;
        }

        // The cadence-vs-budget guard that used to run here now runs in
        // PriceSourcesOptionsValidator, so an overspending configuration fails the process at boot
        // instead of after the host has reported healthy.
        LogBudgetCoverage(polling.Value.PollInterval);

        using var timer = new PeriodicTimer(polling.Value.PollInterval, clock);

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

    /// <summary>
    /// Reports, once per start, how long each backup's budget lasts if it has to serve every poll.
    /// </summary>
    /// <remarks>
    /// Backups are exempt from the boot-time cadence check: they are called only while the sources
    /// above them are down, and holding every source to the full period would peg the feed's
    /// cadence to the smallest budget in the file (D-10). This log is the only place the cost of
    /// that exemption is visible, so it belongs here rather than in the validator — which has no
    /// logger and re-runs on every options rebuild.
    /// </remarks>
    private void LogBudgetCoverage(TimeSpan pollInterval)
    {
        var primary = _enabled[0];
        logger.LogInformation(
            "Primary {Source} at {Interval} ({Polls:F0} of {Limit} requests per period).",
            primary.SourceCode,
            pollInterval,
            PriceSourcesOptionsValidator.LongestPeriod / pollInterval,
            primary.MonthlyRequestLimit);

        foreach (var backup in _enabled.Skip(1))
        {
            // Coverage assumes the worst case the exemption allows: everything above this source
            // is down, so it serves every poll until its budget is spent.
            var coverage = pollInterval * backup.MonthlyRequestLimit;
            logger.LogInformation(
                "Backup {Source}: {Limit} requests = {Days:F1} days of full outage coverage.",
                backup.SourceCode, backup.MonthlyRequestLimit, coverage.TotalDays);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        // Lowest Priority wins. Note this does NOT filter on Enabled or on remaining quota — the
        // failover chain (phase-1-todo item 3) is what makes those decisions; until then a
        // disabled source is still registered and can be selected here.
        var source = scope.ServiceProvider.GetServices<IPriceSource>()
            .OrderBy(s => s.Priority)
            .First();

        // The source is guaranteed to be enabled and not exhausted by the validator, but it may throw if it is exhausted by another process in a scaled-out deployment.
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        // Poll the source and persist the tick. The source may throw if it is exhausted, which is handled by the caller.
        var quote = await source.GetLatestQuoteAsync(PriceSymbols.Gold, ct);
        // Persist the tick and update the source registration with the last success time.
        db.PriceTicks.Add(quote.ToTick());

        // Update the last success time for the source registration. This is used to determine if a source is stale.
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
