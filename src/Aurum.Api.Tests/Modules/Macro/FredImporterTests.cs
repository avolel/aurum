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

    private readonly ListLogger<FredImporter> _logger = new();

    // Deleting the series cascades to its observations.
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.MacroSeries.ExecuteDeleteAsync(Ct);
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

    private IEnumerable<string> Warnings => _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

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
}

/// <summary>The release time rule, with no container.</summary>
public class FredImporterReleasedAtTests
{
    [Fact]
    public void ReleasedAt_is_0830_New_York_in_winter() =>
        Assert.Equal(
            new DateTimeOffset(2025, 2, 12, 13, 30, 0, TimeSpan.Zero),
            FredImporter.ReleasedAt(new DateOnly(2025, 2, 12)));

    [Fact]
    public void ReleasedAt_is_0830_New_York_in_summer() =>
        Assert.Equal(
            new DateTimeOffset(2025, 7, 15, 12, 30, 0, TimeSpan.Zero),
            FredImporter.ReleasedAt(new DateOnly(2025, 7, 15)));

    [Fact]
    public void ReleasedAt_is_null_without_a_release_date() =>
        Assert.Null(FredImporter.ReleasedAt(null));
}
