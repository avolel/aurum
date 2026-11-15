using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing.Macro;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Jobs;

/// <summary>
/// Every <c>Macro:Polling:RefreshInterval</c>, fetches each <see cref="FredSeries"/> from FRED and
/// saves it. A series with no rows loads from <c>HistoryStart</c>.
/// </summary>
/// <remarks>
/// The only macro writer, so <see cref="FredImporter"/>'s load-compare-save stays safe (D-23). BLS's
/// release-morning save must run inside this service, not on a timer of its own.
/// </remarks>
internal sealed class MacroPollingService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<MacroPollingOptions> options,
    ILogger<MacroPollingService> logger) : BackgroundService
{
    // Re-read the last two months, so a late month or a corrected market figure is picked up.
    private const int OverlapDays = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RefreshInterval, clock);

        // Refresh once immediately, then on the cadence.
        do
        {
            foreach (var series in FredSeries.All)
            {
                try
                {
                    await RefreshAsync(series, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One bad series must not stop the others.
                    logger.LogError(ex, "Refreshing {Series} from FRED failed; trying again next pass.", series.Code);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// <see cref="OverlapDays"/> before the newest stored date, or <paramref name="historyStart"/> when
    /// nothing is stored.
    /// </summary>
    internal static DateOnly FetchFrom(DateOnly? newestStored, DateOnly historyStart) =>
        newestStored?.AddDays(-OverlapDays) ?? historyStart;

    private async Task RefreshAsync(FredSeries series, CancellationToken ct)
    {
        // A scope per series: a failed save leaves tracked rows in the DbContext, and the next
        // series' SaveChangesAsync would try to write them again.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        var newest = await db.MacroObservations.AsNoTracking()
            .Where(o => o.Series.Provider == FredSeries.Provider && o.Series.Code == series.Code)
            .MaxAsync(o => (DateOnly?)o.ObservedOn, ct);

        // HistoryStart is checked at startup, so it is set.
        var from = FetchFrom(newest, options.Value.HistoryStart!.Value);

        var rows = await scope.ServiceProvider.GetRequiredService<FredClient>()
            .GetObservationsAsync(series, from, ct);
        var (inserted, updated) = await scope.ServiceProvider.GetRequiredService<FredImporter>()
            .SaveAsync(series, rows, ct);

        logger.LogInformation(
            "FRED {Series} from {From:yyyy-MM-dd}: {Fetched} fetched, {Inserted} inserted, {Updated} updated.",
            series.Code, from, rows.Count, inserted, updated);
    }
}
