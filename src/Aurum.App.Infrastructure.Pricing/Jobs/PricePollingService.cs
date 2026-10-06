using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Jobs;

/// <summary>
/// Polls the failover chain on a schedule and persists canonical ticks.
/// </summary>
/// <remarks>
/// Assumes exactly one poller. If the API scales out, host this separately rather than replicate it.
/// </remarks>
public class PricePollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<PriceSourcesOptions> options,
    TimeProvider clock,
    IOptions<PricePollingOptions> polling,
    ILatestQuoteCache cache,
    IDeltaEngine deltas,
    ILogger<PricePollingService> logger) : BackgroundService
{
    /// <summary>
    /// Enabled sources in failover order, for the coverage log. Same ordering
    /// <see cref="FailoverPriceFeed"/> uses.
    /// </summary>
    private readonly IReadOnlyList<PriceSourceOptions> _enabled =
        options.Value.EnabledInFailoverOrder();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogBudgetCoverage(polling.Value.PollInterval);

        // Awaited here, not a hosted service of its own, so start order cannot break it (D-16).
        // Two reads, because a Sample cannot rebuild a quote (D-17).
        if (!await TryWarmAsync(cache.EnsureWarmAsync, "Latest-quote cache", stoppingToken)
            || !await TryWarmAsync(deltas.EnsureWarmAsync, "Price-move history", stoppingToken))
        {
            return;
        }

        using var timer = new PeriodicTimer(polling.Value.PollInterval, clock);

        // Poll once immediately, then on the cadence.
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (AllSourcesFailedException ex)
                when (ex.AllQuotaExhausted && ex.EarliestResetsAt is { } resetsAt)
            {
                // Every source is out of budget. Sleep to the EARLIEST reset, or funded sources sit idle.
                var wait = resetsAt - clock.GetUtcNow();
                logger.LogError(
                    "Every source is out of quota; pausing polling for {Wait} until {ResetsAt:O}.",
                    wait, resetsAt);

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, clock, stoppingToken);
                }
            }
            catch (AllSourcesFailedException ex)
            {
                // Faults or open circuits clear in minutes, so stay on the cadence.
                logger.LogError(ex, "Every source failed this poll; will retry on the next interval.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Price poll failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Runs one warm-up. Returns false only when the host is stopping.
    /// </summary>
    /// <remarks>
    /// Best effort: an unhandled exception would stop the host over a convenience (D-16, D-17).
    /// </remarks>
    private async Task<bool> TryWarmAsync(
        Func<CancellationToken, Task> warm, string what, CancellationToken stoppingToken)
    {
        try
        {
            await warm(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{What} warm-up failed; it fills from the first poll instead.", what);
        }

        return true;
    }

    /// <summary>
    /// Reports, once per start, how long each backup's budget lasts if it has to serve every poll.
    /// </summary>
    /// <remarks>
    /// The only place the cost of exempting backups from the startup check is visible (D-10).
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
            // Worst case: everything above it is down.
            var coverage = pollInterval * backup.MonthlyRequestLimit;
            logger.LogInformation(
                "Backup {Source}: {Limit} requests = {Days:F1} days of full outage coverage.",
                backup.SourceCode, backup.MonthlyRequestLimit, coverage.TotalDays);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var feed = scope.ServiceProvider.GetRequiredService<IPriceFeed>();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        PriceFeedResult result;
        try
        {
            result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, ct);
        }
        catch (AllSourcesFailedException ex)
        {
            // Save before rethrowing: price_sources is where an operator sees why the feed is dark.
            await ProjectAttemptsAsync(db, ex.Attempts, ct);
            await db.SaveChangesAsync(ct);
            throw;
        }

        // Memory before the database, so a failed save cannot hide a fetched price (D-16).
        cache.Record(result);
        deltas.Record(result.Quote);

        db.PriceTicks.Add(result.Quote.ToTick());
        await ProjectAttemptsAsync(db, result.Attempts, ct);
        await db.SaveChangesAsync(ct);

        if (result.UsedFallback)
        {
            logger.LogWarning(
                "Primary {Primary} did not serve this poll; {Actual} did after {Attempts}.",
                result.PrimarySourceCode, result.Quote.SourceCode,
                string.Join(" -> ", result.AttemptedSources));
        }

        logger.LogInformation(
            "Tick {Symbol} mid={Mid} from {Source}, {StalenessMs}ms stale.",
            result.Quote.Symbol, result.Quote.Mid, result.Quote.SourceCode,
            (result.Quote.ReceivedAt - result.Quote.ObservedAt).TotalMilliseconds);
    }

    /// <summary>
    /// Writes one poll's per-source outcomes onto <c>price_sources</c> for operators.
    /// </summary>
    /// <remarks>
    /// Success leaves the last failure in place; compare <c>LastFailureAt</c> with
    /// <c>LastSuccessAt</c> to tell current from past. A skipped source writes nothing, so the fault
    /// that opened its circuit is kept. <c>IsEnabled</c> is never written: configuration decides.
    /// </remarks>
    private async Task ProjectAttemptsAsync(
        AurumDbContext db, IReadOnlyList<SourceAttempt> attempts, CancellationToken ct)
    {
        var codes = attempts.Select(a => a.SourceCode).ToList();

        // One query per poll, not one per attempt.
        var registrations = await db.PriceSources
            .Where(source => codes.Contains(source.Code))
            .ToDictionaryAsync(source => source.Code, ct);

        foreach (var attempt in attempts)
        {
            if (!registrations.TryGetValue(attempt.SourceCode, out var registration))
            {
                // Missing from the seed. Not worth failing the poll over.
                continue;
            }

            switch (attempt.Outcome)
            {
                case SourceAttemptOutcome.Success:
                    registration.LastSuccessAt = clock.GetUtcNow();
                    break;

                case SourceAttemptOutcome.Faulted:
                case SourceAttemptOutcome.QuotaExhausted:
                    registration.LastFailureAt = clock.GetUtcNow();
                    registration.LastFailureReason = attempt.FailureReason;
                    break;

                case SourceAttemptOutcome.SkippedCircuitOpen:
                default:
                    break;
            }
        }
    }
}
