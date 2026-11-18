using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Macro;
using Aurum.App.Infrastructure.Pricing.Macro;
using Aurum.App.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Jobs;

/// <summary>
/// Every <c>Macro:Polling:RefreshInterval</c>, fetches each <see cref="FredSeries"/> from FRED and
/// saves it, then adds each <see cref="FredRelease"/>'s coming dates to <c>macro_releases</c>. Between
/// passes it wakes for each calendar row: BLS a minute after a CPI or jobs release, FRED an hour after
/// every release (D-26).
/// </summary>
/// <remarks>
/// The only macro writer, so <see cref="FredImporter"/>'s load-compare-save stays safe (D-23). Every
/// write, BLS's included, happens on this service's one loop.
/// </remarks>
internal sealed class MacroPollingService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<MacroPollingOptions> options,
    IAppLogService<MacroPollingService> logger) : BackgroundService
{
    // Re-read the last two months, so a late month or a corrected market figure is picked up.
    private const int OverlapDays = 60;

    // BLS publishes at the release time; FRED 40 minutes to 2 hours later (D-22).
    private static readonly TimeSpan BlsDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FredDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan BlsRetryEvery = TimeSpan.FromMinutes(1);
    private const int BlsAttempts = 15;

    /// <summary>One wake-up for a calendar row: BLS for the new month, or FRED for the release's series.</summary>
    internal sealed record ReleaseFetch(FredRelease Release, DateTimeOffset ScheduledAt, bool FromBls)
    {
        public DateTimeOffset DueAt => ScheduledAt + (FromBls ? BlsDelay : FredDelay);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPass = clock.GetUtcNow();
        // Fetches due before startup are skipped: a restart at 08:40 misses that morning's BLS read,
        // and the 09:30 FRED read or the next pass fills the month instead.
        var handledUpTo = clock.GetUtcNow();

        try
        {
            while (true)
            {
                if (clock.GetUtcNow() >= nextPass)
                {
                    await RunPassAsync(stoppingToken);
                    nextPass = clock.GetUtcNow() + options.Value.RefreshInterval;
                }

                // Read after the pass, so dates it just added are seen.
                var now = clock.GetUtcNow();
                var fetches = await LoadFetchesAsync(handledUpTo, stoppingToken);
                if (fetches is not null)
                {
                    foreach (var fetch in fetches.TakeWhile(f => f.DueAt <= now))
                    {
                        await RunFetchAsync(fetch, stoppingToken);
                    }

                    // Not the time after the fetches: one due while BLS was retrying runs next loop.
                    handledUpTo = now;
                }

                var wake = (fetches ?? []).Select(f => f.DueAt).Where(t => t > now).Append(nextPass).Min();
                var wait = wake - clock.GetUtcNow();
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// <see cref="OverlapDays"/> before the newest stored date, or <paramref name="historyStart"/> when
    /// nothing is stored.
    /// </summary>
    internal static DateOnly FetchFrom(DateOnly? newestStored, DateOnly historyStart) =>
        newestStored?.AddDays(-OverlapDays) ?? historyStart;

    /// <summary>
    /// The fetches for <paramref name="rows"/> due after <paramref name="after"/>, earliest first. A row
    /// whose code is no longer in <see cref="FredRelease.All"/> is skipped.
    /// </summary>
    internal static IReadOnlyList<ReleaseFetch> Fetches(IEnumerable<MacroRelease> rows, DateTimeOffset after) =>
        rows.Select(r => (Row: r, Release: FredRelease.All.FirstOrDefault(x => x.Code == r.ReleaseCode)))
            .Where(x => x.Release is not null)
            .SelectMany(x => BlsSeriesOf(x.Release!).Count > 0
                ? new[] { new ReleaseFetch(x.Release!, x.Row.ScheduledAt, FromBls: true), new ReleaseFetch(x.Release!, x.Row.ScheduledAt, FromBls: false) }
                : [new ReleaseFetch(x.Release!, x.Row.ScheduledAt, FromBls: false)])
            .Where(f => f.DueAt > after)
            .OrderBy(f => f.DueAt)
            .ToList();

    /// <summary>
    /// Per series, BLS's newest month with a value, kept only when it is later than the newest month
    /// stored. Older months are left alone: they are revised values and would overwrite FRED's first
    /// published ones (D-23).
    /// </summary>
    internal static IReadOnlyList<BlsObservation> NewMonths(
        IReadOnlyList<BlsObservation> fetched, IReadOnlyDictionary<FredSeries, DateOnly?> newestStored) =>
        fetched.Where(r => r.Value is not null)
            .GroupBy(r => r.Series)
            .Select(g => g.MaxBy(r => r.ObservedOn)!)
            .Where(r => newestStored[r.Series.Figure] is not { } stored || r.ObservedOn > stored)
            .ToList();

    private static List<BlsSeries> BlsSeriesOf(FredRelease release) =>
        BlsSeries.All.Where(b => release.Series.Contains(b.Figure)).ToList();

    private async Task RunPassAsync(CancellationToken ct)
    {
        foreach (var series in FredSeries.All)
        {
            await TryAsync(() => RefreshAsync(series, ct),
                $"Refreshing {series.Code} from FRED failed; trying again next pass.", ct);
        }

        foreach (var release in FredRelease.All)
        {
            await TryAsync(() => RefreshReleaseDatesAsync(release, ct),
                $"Refreshing release {release.Code}'s dates from FRED failed; trying again next pass.", ct);
        }
    }

    /// <returns>Null when the calendar can't be read; the caller then waits for the next pass.</returns>
    private async Task<IReadOnlyList<ReleaseFetch>?> LoadFetchesAsync(DateTimeOffset after, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

            // FredDelay is the longest offset, so earlier rows have nothing left to run.
            var from = after - FredDelay;
            var rows = await db.MacroReleases.AsNoTracking().Where(r => r.ScheduledAt > from).ToListAsync(ct);
            return Fetches(rows, after);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await logger.LogErrorAsync("MacroPollingService", ex,
                details: "Reading macro_releases failed; trying again at the next pass.",
                logType: LogType.SystemLog, ct: ct);
            return null;
        }
    }

    private async Task RunFetchAsync(ReleaseFetch fetch, CancellationToken ct)
    {
        if (fetch.FromBls)
        {
            await TryAsync(() => FetchFromBlsAsync(fetch, ct),
                $"Release {fetch.Release.Code} at {fetch.ScheduledAt:u}: BLS fetch failed; FRED's fetch an hour after the release still runs.", ct);
            return;
        }

        foreach (var series in fetch.Release.Series)
        {
            await TryAsync(() => RefreshAsync(series, ct),
                $"Release {fetch.Release.Code} at {fetch.ScheduledAt:u}: refreshing {series.Code} from FRED failed; trying again next pass.", ct);
        }
    }

    /// <summary>
    /// Saves BLS's new month for each of the release's series, trying each minute until every series
    /// has it or <see cref="BlsAttempts"/> run out.
    /// </summary>
    private async Task FetchFromBlsAsync(ReleaseFetch fetch, CancellationToken ct)
    {
        var day = FredRelease.NewYorkDay(fetch.ScheduledAt);
        // The figure is for the month before: January's release asks for last year.
        var year = day.AddMonths(-1).Year;
        var waiting = BlsSeriesOf(fetch.Release);

        for (var attempt = 1; ; attempt++)
        {
            // A scope per attempt, for the same reason as RefreshAsync.
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();
            var importer = scope.ServiceProvider.GetRequiredService<FredImporter>();

            var stored = new Dictionary<FredSeries, DateOnly?>();
            foreach (var series in waiting)
            {
                stored[series.Figure] = await NewestStoredAsync(db, series.Figure, ct);
            }

            var fetched = await scope.ServiceProvider.GetRequiredService<BlsClient>()
                .GetObservationsAsync(waiting, year, ct);
            var fresh = NewMonths(fetched, stored);

            // Released on the calendar day, so ReleasedAt equals this row's ScheduledAt and FRED's
            // later row, published the same day, matches it.
            foreach (var row in fresh)
            {
                await importer.SaveAsync(row.Series.Figure, [new FredObservation(row.ObservedOn, row.Value, day)], ct);
            }

            waiting = waiting.Except(fresh.Select(r => r.Series)).ToList();

            await logger.LogAsync(LogType.SystemLog,
                "FetchedBlsRelease",
                $"BLS {fetch.Release.Code} on {day:yyyy-MM-dd}, attempt {attempt}: saved [{string.Join(", ", fresh.Select(r => $"{r.Series.Figure.Code} {r.ObservedOn:yyyy-MM}={r.Value}"))}], waiting for [{string.Join(", ", waiting.Select(s => s.Figure.Code))}].",
                level: LogLevel.Information,
                ct: ct);

            if (waiting.Count == 0)
            {
                return;
            }

            if (attempt == BlsAttempts)
            {
                await logger.LogAsync(LogType.SystemLog,
                    "BlsFigureMissing",
                    $"BLS {fetch.Release.Code} on {day:yyyy-MM-dd}: no new month after {BlsAttempts} attempts for [{string.Join(", ", waiting.Select(s => s.Figure.Code))}]; FRED fills it later.",
                    level: LogLevel.Warning,
                    ct: ct);
                return;
            }

            await Task.Delay(BlsRetryEvery, clock, ct);
        }
    }

    private async Task RefreshAsync(FredSeries series, CancellationToken ct)
    {
        // A scope per series: a failed save leaves tracked rows in the DbContext, and the next
        // series' SaveChangesAsync would try to write them again.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        // HistoryStart is checked at startup, so it is set.
        var from = FetchFrom(await NewestStoredAsync(db, series, ct), options.Value.HistoryStart!.Value);

        var rows = await scope.ServiceProvider.GetRequiredService<FredClient>()
            .GetObservationsAsync(series, from, ct);
        var (inserted, updated) = await scope.ServiceProvider.GetRequiredService<FredImporter>()
            .SaveAsync(series, rows, ct);

        await logger.LogAsync(LogType.SystemLog,
            "RefreshedFredSeries",
            $"FRED {series.Code} from {from:yyyy-MM-dd}: {rows.Count} fetched, {inserted} inserted, {updated} updated.",
            level: LogLevel.Information,
            ct: ct
        );
    }

    private static Task<DateOnly?> NewestStoredAsync(AurumDbContext db, FredSeries series, CancellationToken ct) =>
        db.MacroObservations.AsNoTracking()
            .Where(o => o.Series.Provider == FredSeries.Provider && o.Series.Code == series.Code)
            .MaxAsync(o => (DateOnly?)o.ObservedOn, ct);

    private async Task RefreshReleaseDatesAsync(FredRelease release, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        // From today in UTC. After 19:00 New York that is tomorrow, but today's releases are past by then.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var dates = await scope.ServiceProvider.GetRequiredService<FredClient>()
            .GetReleaseDatesAsync(release, today, ct);
        var inserted = await scope.ServiceProvider.GetRequiredService<FredImporter>()
            .SaveReleaseDatesAsync(release, dates, ct);

        await logger.LogAsync(LogType.SystemLog,
            "RefreshedFredReleaseDates",
            $"FRED release {release.Code} from {today:yyyy-MM-dd}: {dates.Count} dates, {inserted} new.",
            level: LogLevel.Information,
            ct: ct
        );
    }

    /// <summary>Runs one unit of work; a failure is logged so the others carry on.</summary>
    private async Task TryAsync(Func<Task> work, string failure, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await logger.LogErrorAsync("MacroPollingService", ex, details: failure, logType: LogType.SystemLog, ct: ct);
        }
    }
}
