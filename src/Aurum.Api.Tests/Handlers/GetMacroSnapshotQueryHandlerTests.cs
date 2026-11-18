using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.Macro.DTOs;
using Aurum.App.Application.Macro.Handlers.Queries;
using Aurum.App.Application.Macro.Queries;
using Aurum.App.Infrastructure.Data.Entities.Macro;
using Aurum.App.Infrastructure.Pricing.Macro;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Handlers;

/// <remarks>
/// Real Postgres: the subject is the SQL the "newest known row per series" filter becomes. Macro
/// tables have no retention job, so fixed dates are safe.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class GetMacroSnapshotQueryHandlerTests(PostgresFixture fixture) : IAsyncLifetime
{
    // October 2026 CPI morning, before the 08:30 New York (13:30Z) release.
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OctCpiOut = new(2026, 11, 13, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SeptCpiOut = new(2026, 10, 24, 12, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly Sept = new(2026, 9, 1);
    private static readonly DateOnly Oct = new(2026, 10, 1);

    // Deleting the series cascades to its observations.
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.MacroSeries.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(FredSeries series, params (DateOnly ObservedOn, decimal? Value, DateTimeOffset? ReleasedAt)[] rows)
    {
        await using var db = fixture.CreateDbContext();
        db.MacroSeries.Add(new MacroSeries
        {
            Provider = FredSeries.Provider,
            Code = series.Code,
            Title = series.Title,
            Units = series.Units,
            Frequency = series.Frequency,
            Observations = rows
                .Select(r => new MacroObservation { ObservedOn = r.ObservedOn, Value = r.Value, ReleasedAt = r.ReleasedAt })
                .ToList(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<MacroFigureDto>> HandleAsync(DateTimeOffset? asOf = null)
    {
        var clock = new FakeTimeProvider(Now);
        await using var db = fixture.CreateDbContext(clock);
        return await new GetMacroSnapshotQueryHandler(db, clock).Handle(new GetMacroSnapshotQuery(asOf), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WithSeveralRows_ReturnsTheNewestPerSeriesInFredSeriesOrder()
    {
        // Market series seeded first, so insertion order and FredSeries.All order disagree.
        await SeedAsync(FredSeries.Treasury10Y,
            (new DateOnly(2026, 11, 11), 4.10m, null),
            (new DateOnly(2026, 11, 12), 4.15m, null));
        await SeedAsync(FredSeries.Cpi,
            (new DateOnly(2026, 8, 1), 330.1m, new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.Zero)),
            (Sept, 331.2m, SeptCpiOut));

        var figures = await HandleAsync();

        Assert.Equal([FredSeries.Cpi.Code, FredSeries.Treasury10Y.Code], figures.Select(f => f.Code));
        Assert.Equal(
            new MacroFigureDto(FredSeries.Cpi.Code, FredSeries.Cpi.Title, FredSeries.Cpi.Units, 331.2m, Sept, SeptCpiOut, AgeDays: 20),
            figures[0]);
        Assert.Equal(
            new MacroFigureDto(FredSeries.Treasury10Y.Code, FredSeries.Treasury10Y.Title, FredSeries.Treasury10Y.Units,
                4.15m, new DateOnly(2026, 11, 12), null, AgeDays: 1),
            figures[1]);
    }

    [Fact]
    public async Task Handle_NewestRowHasNoValue_SkipsItForTheOneBefore()
    {
        await SeedAsync(FredSeries.Cpi, (Sept, 331.2m, SeptCpiOut), (Oct, null, SeptCpiOut.AddDays(1)));

        var figure = Assert.Single(await HandleAsync());

        Assert.Equal(Sept, figure.ObservedOn);
    }

    [Fact]
    public async Task Handle_FigureReleasedAfterAsOf_IsLeftOut()
    {
        await SeedAsync(FredSeries.Cpi, (Sept, 331.2m, SeptCpiOut), (Oct, 332.0m, OctCpiOut));

        var before = Assert.Single(await HandleAsync(OctCpiOut.AddTicks(-10))); // 1 µs before; Postgres keeps µs
        var atRelease = Assert.Single(await HandleAsync(OctCpiOut));

        Assert.Equal(Sept, before.ObservedOn);
        Assert.Equal(Oct, atRelease.ObservedOn);
        Assert.Equal(0, atRelease.AgeDays);
    }

    [Fact]
    public async Task Handle_MarketFigureForAsOfsOwnDay_IsLeftOut()
    {
        await SeedAsync(FredSeries.Treasury10Y,
            (new DateOnly(2026, 11, 12), 4.15m, null),
            (new DateOnly(2026, 11, 13), 4.20m, null));

        var figure = Assert.Single(await HandleAsync());

        Assert.Equal(new DateOnly(2026, 11, 12), figure.ObservedOn);
    }

    [Fact]
    public async Task Handle_MarketFigure_UsesAsOfsNewYorkDateNotUtc()
    {
        // 02:00Z on the 13th is 21:00 on the 12th in New York, so the 12th isn't over yet.
        await SeedAsync(FredSeries.Treasury10Y,
            (new DateOnly(2026, 11, 11), 4.10m, null),
            (new DateOnly(2026, 11, 12), 4.15m, null));

        var figure = Assert.Single(await HandleAsync(new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero)));

        Assert.Equal(new DateOnly(2026, 11, 11), figure.ObservedOn);
        Assert.Equal(1, figure.AgeDays);
    }

    [Fact]
    public async Task Handle_AsOfWithANonZeroOffset_ReturnsTheSameAsUtc()
    {
        // Npgsql refuses a non-zero offset for timestamptz.
        await SeedAsync(FredSeries.Cpi, (Sept, 331.2m, SeptCpiOut), (Oct, 332.0m, OctCpiOut));

        var figure = Assert.Single(await HandleAsync(OctCpiOut.ToOffset(TimeSpan.FromHours(-5))));

        Assert.Equal(Oct, figure.ObservedOn);
    }

    [Fact]
    public async Task Handle_WithNoAsOf_UsesTheInjectedClock()
    {
        // Released after the fake clock. Visible if the handler reads the wall clock instead.
        await SeedAsync(FredSeries.Cpi, (Sept, 331.2m, SeptCpiOut), (Oct, 332.0m, OctCpiOut));

        var figure = Assert.Single(await HandleAsync(asOf: null));

        Assert.Equal(Sept, figure.ObservedOn);
    }

    [Fact]
    public async Task Handle_WithAnEmptyDatabase_ReturnsAnEmptyList()
    {
        Assert.Empty(await HandleAsync());
    }
}
