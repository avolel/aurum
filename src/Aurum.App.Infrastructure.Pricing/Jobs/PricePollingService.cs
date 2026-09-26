using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Jobs;

/// <summary>
/// Polls the failover chain on a schedule and persists canonical ticks.
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
    /// Enabled sources in failover order, so the head is the primary. A statement about
    /// <em>budgets</em>, for the coverage log below — the chain that actually runs is built by
    /// <see cref="FailoverPriceFeed"/>. Both come from
    /// <see cref="PriceSourcesOptions.EnabledInFailoverOrder"/>, which is what makes this log
    /// describe the chain rather than a plausible-looking parallel ordering.
    /// </summary>
    private readonly IReadOnlyList<PriceSourceOptions> _enabled =
        options.Value.EnabledInFailoverOrder();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PriceSourcesOptionsValidator refuses a configuration with no enabled source, so this is
        // a backstop for a host that skipped validation rather than an expected state.
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
            catch (AllSourcesFailedException ex)
                when (ex.AllQuotaExhausted && ex.EarliestResetsAt is { } resetsAt)
            {
                // Every source is out of budget, so there is nothing to retry until the earliest
                // period rolls. EARLIEST, not the one that failed last: sleeping to the latest
                // reset idles funded providers for up to a month. A bare QuotaExhaustedException
                // can no longer reach here at all — the feed records each one and moves on, which
                // is the whole point of item 3.
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
                // Faults, open circuits, or a mix of those with quota. All of them clear before a
                // quota period rolls, so stay on the cadence rather than sleeping. Deleting this
                // clause silently promotes every total outage to the sleep above.
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
        var feed = scope.ServiceProvider.GetRequiredService<IPriceFeed>();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        PriceFeedResult result;
        try
        {
            result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, ct);
        }
        catch (AllSourcesFailedException ex)
        {
            // Project before rethrowing. Circuit state is authoritative but in-memory, so
            // price_sources is the only place an operator can see why the feed is dark — and a
            // total outage is exactly when they go looking.
            await ProjectAttemptsAsync(db, ex.Attempts, ct);
            await db.SaveChangesAsync(ct);
            throw;
        }

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
    /// Writes one poll's per-source outcomes onto <c>price_sources</c>, the operator-facing
    /// projection of what the chain just observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Success no longer clears <c>LastFailureReason</c>.</b> It used to, while leaving
    /// <c>LastFailureAt</c> set, which produced rows asserting a failure at a timestamp with no
    /// reason for it. Both now persist as the record of the last failure; a reader tells current
    /// from historical by comparing <c>LastFailureAt</c> against <c>LastSuccessAt</c>.
    /// </para>
    /// <para>
    /// <see cref="SourceAttemptOutcome.SkippedCircuitOpen"/> writes nothing at all. The source was
    /// not called, so it produced no new evidence, and stamping "circuit open" over the reason
    /// would erase the fault that opened the circuit — the one thing the operator needs.
    /// </para>
    /// <para>
    /// This never writes <c>PriceSource.IsEnabled</c>. Configuration is authoritative for whether
    /// a source is in the chain; the column is descriptive and currently has no reader.
    /// </para>
    /// </remarks>
    private async Task ProjectAttemptsAsync(
        AurumDbContext db, IReadOnlyList<SourceAttempt> attempts, CancellationToken ct)
    {
        if (attempts.Count == 0)
        {
            return;
        }

        var codes = attempts.Select(a => a.SourceCode).ToList();

        // One tracked query for the whole poll rather than a FindAsync per attempt: the chain is
        // short, but this runs on every tick forever.
        var registrations = await db.PriceSources
            .Where(source => codes.Contains(source.Code))
            .ToDictionaryAsync(source => source.Code, ct);

        foreach (var attempt in attempts)
        {
            if (!registrations.TryGetValue(attempt.SourceCode, out var registration))
            {
                // Registered in code and configured, but absent from the seed. Not worth failing
                // the poll over — the tick is already in the change tracker.
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
