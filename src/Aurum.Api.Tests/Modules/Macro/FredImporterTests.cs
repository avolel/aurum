using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data.Entities.Macro;
using Aurum.App.Infrastructure.Pricing.Macro;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aurum.Api.Tests.Modules.Macro;

/// <remarks>Each save uses a fresh <c>DbContext</c>, as each poll would.</remarks>
[Collection(PostgresCollection.Name)]
public class FredImporterTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateOnly Sept = new(2025, 9, 1);
    private static readonly DateOnly Aug = new(2025, 8, 1);

    private readonly ListAppLog<FredImporter> _logger = new();

    // Deleting the series cascades to its observations.
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.MacroSeries.ExecuteDeleteAsync(Ct);
        await db.MacroReleases.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int Inserted, int Updated)> SaveAsync(FredSeries series, params FredObservation[] rows)
    {
        await using var db = fixture.CreateDbContext();
        return await new FredImporter(db, _logger).SaveAsync(series, rows, Ct);
    }

    private async Task<List<MacroObservation>> StoredAsync(FredSeries series)
    {
        await using var db = fixture.CreateDbContext();
        return await db.MacroObservations.AsNoTracking()
            .Where(o => o.Series.Code == series.Code)
            .OrderBy(o => o.ObservedOn)
            .ToListAsync(Ct);
    }

    private IEnumerable<string> Warnings => _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Details!);

    [Fact]
    public async Task Creates_the_series_row_once()
    {
        await SaveAsync(FredSeries.Cpi, new FredObservation(Aug, 322.1m, new DateOnly(2025, 9, 11)));
        await SaveAsync(FredSeries.Cpi, new FredObservation(Sept, 323.4m, new DateOnly(2025, 10, 24)));

        await using var db = fixture.CreateDbContext();
        var series = await db.MacroSeries.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((FredSeries.Provider, FredSeries.Cpi.Code, FredSeries.Cpi.Title, FredSeries.Cpi.Units, FredSeries.Cpi.Frequency),
            (series.Provider, series.Code, series.Title, series.Units, series.Frequency));
    }

    [Fact]
    public async Task Inserts_new_observations()
    {
        var result = await SaveAsync(FredSeries.Cpi,
            new FredObservation(Aug, 322.1m, new DateOnly(2025, 9, 11)),
            new FredObservation(Sept, 323.4m, new DateOnly(2025, 10, 24)));

        Assert.Equal((2, 0), result);
        var stored = await StoredAsync(FredSeries.Cpi);
        Assert.Equal([Aug, Sept], stored.Select(o => o.ObservedOn));
        Assert.Equal([322.1m, 323.4m], stored.Select(o => o.Value));
    }

    [Fact]
    public async Task Second_run_with_same_rows_writes_nothing()
    {
        var rows = new[]
        {
            new FredObservation(Aug, 322.1m, new DateOnly(2025, 9, 11)),
            new FredObservation(Sept, 323.4m, new DateOnly(2025, 10, 24)),
        };
        await SaveAsync(FredSeries.Cpi, rows);
        var before = await StoredAsync(FredSeries.Cpi);

        var result = await SaveAsync(FredSeries.Cpi, rows);

        Assert.Equal((0, 0), result);
        // UpdatedAt unchanged proves no UPDATE ran, not only that the counter says so.
        Assert.Equal(before.Select(o => o.UpdatedAt), (await StoredAsync(FredSeries.Cpi)).Select(o => o.UpdatedAt));
    }

    [Fact]
    public async Task Changed_value_on_release_series_overwrites_and_warns()
    {
        var releasedOn = new DateOnly(2025, 10, 24);
        await SaveAsync(FredSeries.Cpi, new FredObservation(Sept, 323.4m, releasedOn));

        var result = await SaveAsync(FredSeries.Cpi, new FredObservation(Sept, 323.5m, releasedOn));

        Assert.Equal((0, 1), result);
        Assert.Equal(323.5m, (await StoredAsync(FredSeries.Cpi)).Single().Value);
        var warning = Assert.Single(Warnings);
        Assert.Contains(FredSeries.Cpi.Code, warning);
        Assert.Contains("323.4", warning);
    }

    [Fact]
    public async Task Changed_value_on_market_series_overwrites_without_warning()
    {
        var day = new DateOnly(2025, 9, 2);
        await SaveAsync(FredSeries.Treasury10Y, new FredObservation(day, 4.26m, null));

        var result = await SaveAsync(FredSeries.Treasury10Y, new FredObservation(day, 4.28m, null));

        Assert.Equal((0, 1), result);
        Assert.Equal(4.28m, (await StoredAsync(FredSeries.Treasury10Y)).Single().Value);
        Assert.Empty(Warnings);
    }

    [Fact]
    public async Task Null_value_is_kept_as_a_row()
    {
        // October 2025 CPI is "." on FRED (see fred-cpiaucsl-initial-release.json).
        var oct = new DateOnly(2025, 10, 1);
        await SaveAsync(FredSeries.Cpi, new FredObservation(oct, null, new DateOnly(2025, 12, 18)));

        var stored = Assert.Single(await StoredAsync(FredSeries.Cpi));
        Assert.Equal(oct, stored.ObservedOn);
        Assert.Null(stored.Value);
    }

    [Fact]
    public async Task Market_series_has_null_ReleasedAt()
    {
        await SaveAsync(FredSeries.Treasury10Y, new FredObservation(new DateOnly(2025, 9, 2), 4.26m, null));

        Assert.Null(Assert.Single(await StoredAsync(FredSeries.Treasury10Y)).ReleasedAt);
    }

    private async Task<int> SaveDatesAsync(FredRelease release, params DateOnly[] dates)
    {
        await using var db = fixture.CreateDbContext();
        return await new FredImporter(db, _logger).SaveReleaseDatesAsync(release, dates, Ct);
    }

    private async Task<List<MacroRelease>> StoredReleasesAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.MacroReleases.AsNoTracking().OrderBy(r => r.ScheduledAt).ToListAsync(Ct);
    }

    [Fact]
    public async Task Release_dates_are_stored_at_the_release_time()
    {
        var inserted = await SaveDatesAsync(FredRelease.Cpi, new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 10));

        Assert.Equal(2, inserted);
        // 08:30 EST is 13:30 UTC.
        Assert.Equal(
            [("cpi", new DateTimeOffset(2026, 11, 10, 13, 30, 0, TimeSpan.Zero)), ("cpi", new DateTimeOffset(2026, 12, 10, 13, 30, 0, TimeSpan.Zero))],
            (await StoredReleasesAsync()).Select(r => (r.ReleaseCode, r.ScheduledAt)));
    }

    [Fact]
    public async Task Release_dates_already_stored_are_skipped()
    {
        await SaveDatesAsync(FredRelease.Cpi, new DateOnly(2026, 11, 10));

        var inserted = await SaveDatesAsync(FredRelease.Cpi, new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 10));

        Assert.Equal(1, inserted);
        Assert.Equal(2, (await StoredReleasesAsync()).Count);
    }

    [Fact]
    public async Task FomcCalendar_meeting_is_stored_at_1400_New_York()
    {
        await using (var db = fixture.CreateDbContext())
        {
            await new FredImporter(db, _logger).SaveReleaseTimesAsync(
                FomcCalendar.Code, [FomcCalendar.At(new DateOnly(2026, 12, 9))], Ct);
        }

        // 14:00 EST is 19:00 UTC.
        var stored = Assert.Single(await StoredReleasesAsync());
        Assert.Equal(("fomc", new DateTimeOffset(2026, 12, 9, 19, 0, 0, TimeSpan.Zero)), (stored.ReleaseCode, stored.ScheduledAt));
    }

    [Fact]
    public async Task Same_date_for_two_releases_is_two_rows()
    {
        // CPI and jobs can land on the same morning; the unique key includes the release.
        await SaveDatesAsync(FredRelease.Cpi, new DateOnly(2026, 12, 4));
        await SaveDatesAsync(FredRelease.Jobs, new DateOnly(2026, 12, 4));

        Assert.Equal(["cpi", "jobs"], (await StoredReleasesAsync()).Select(r => r.ReleaseCode).Order());
    }
}

/// <summary>The release time rule, with no container. The New York conversion is in <c>FredReleaseTests</c>.</summary>
public class FredImporterReleasedAtTests
{
    [Fact]
    public void ReleasedAt_uses_the_series_release_time() =>
        Assert.Equal(
            FredRelease.Jobs.At(new DateOnly(2025, 2, 7)),
            FredImporter.ReleasedAt(FredSeries.NonfarmPayrolls, new DateOnly(2025, 2, 7)));

    [Fact]
    public void ReleasedAt_is_null_without_a_release_date() =>
        Assert.Null(FredImporter.ReleasedAt(FredSeries.Treasury10Y, null));
}
