using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Handlers.Queries;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;

namespace Aurum.Api.Tests.Handlers;

/// <remarks>
/// Real Postgres: the subject is the SQL the keyset filter becomes. <c>price_events</c> has no
/// retention, so fixed dates are safe. <c>DeltaPercent</c> doubles as a row label.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class GetPriceEventsQueryHandlerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.PriceEvents.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>One save per call, so Ids follow call order.</summary>
    private async Task SeedAsync(
        DateTimeOffset detectedAt, decimal deltaPercent, string window = "5m", string symbol = SupportedSymbol.Gold)
    {
        await using var db = fixture.CreateDbContext();

        // WindowEndedAt is unique per (Symbol, WindowCode); offset by the label to keep rows distinct.
        var endedAt = detectedAt.AddTicks((long)(deltaPercent * 1000) * 10);
        db.PriceEvents.Add(new PriceEvent
        {
            Symbol = symbol,
            DetectedAt = detectedAt,
            WindowCode = window,
            WindowStartedAt = endedAt.AddMinutes(-5),
            WindowEndedAt = endedAt,
            Direction = deltaPercent >= 0 ? PriceEventDirection.Up : PriceEventDirection.Down,
            StartMid = 2650m,
            EndMid = 2650m * (1 + deltaPercent / 100),
            DeltaAbsolute = 2650m * deltaPercent / 100,
            DeltaPercent = deltaPercent,
            VelocityPercentPerMinute = deltaPercent / 5,
            SampleCount = 2,
            SourceCode = GoldApiIoSource.SourceCode,
            BaselineSourceCode = GoldApiIoSource.SourceCode,
            ThresholdProfile = "default",
            TriggeredRule = $"|{window}| >= 0.25%",
        });
        await db.SaveChangesAsync();
    }

    private async Task<CursorPage<PriceEventDto>> HandleAsync(
        int? limit = null, string? cursor = null, string symbol = SupportedSymbol.Gold,
        string? window = null, decimal? minPercent = null)
    {
        await using var db = fixture.CreateDbContext();

        return await new GetPriceEventsQueryHandler(db).Handle(
            new GetPriceEventsQuery(symbol, window, minPercent, limit, cursor), CancellationToken.None);
    }

    private async Task<List<decimal>> WalkAllPagesAsync(int limit)
    {
        var labels = new List<decimal>();
        string? cursor = null;

        for (var pages = 0; pages < 20; pages++)
        {
            var page = await HandleAsync(limit, cursor);
            labels.AddRange(page.Items.Select(e => e.DeltaPercent));

            if ((cursor = page.NextCursor) is null)
            {
                return labels;
            }
        }

        Assert.Fail("Still returning a NextCursor after 20 pages.");
        return labels;
    }

    [Fact]
    public async Task Handle_FirstPage_ReturnsNewestFirstUpToLimitWithACursor()
    {
        await SeedAsync(T0.AddMinutes(10), 0.31m);
        await SeedAsync(T0.AddMinutes(30), 0.33m);
        await SeedAsync(T0.AddMinutes(20), 0.32m);

        var page = await HandleAsync(limit: 2);

        Assert.Equal([0.33m, 0.32m], page.Items.Select(e => e.DeltaPercent));
        Assert.NotNull(page.NextCursor);
        Assert.Equal(T0.AddMinutes(30), page.Items[0].DetectedAt);
        Assert.Equal("|5m| >= 0.25%", page.Items[0].TriggeredRule);
    }

    [Fact]
    public async Task Handle_NextPageViaCursor_ContinuesBeforeTheLastRowAndEnds()
    {
        await SeedAsync(T0.AddMinutes(10), 0.31m);
        await SeedAsync(T0.AddMinutes(20), 0.32m);
        await SeedAsync(T0.AddMinutes(30), 0.33m);

        var first = await HandleAsync(limit: 2);
        var second = await HandleAsync(limit: 2, cursor: first.NextCursor);

        Assert.Equal([0.31m], second.Items.Select(e => e.DeltaPercent));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Handle_EventsFromOnePollSplitAcrossAPage_NeitherRepeatNorSkip()
    {
        // One poll fires several windows at once: same DetectedAt, ascending Ids.
        await SeedAsync(T0.AddMinutes(30), 0.40m, window: "1h");
        await SeedAsync(T0.AddMinutes(20), 0.31m, window: "5m");
        await SeedAsync(T0.AddMinutes(20), 0.32m, window: "15m");
        await SeedAsync(T0.AddMinutes(10), 0.30m, window: "5m");

        // Newest first, so the later Id (0.32) comes before 0.31 at the shared time.
        Assert.Equal([0.40m, 0.32m, 0.31m, 0.30m], await WalkAllPagesAsync(limit: 2));
    }

    [Fact]
    public async Task Handle_ExactlyLimitRowsLeft_HasNoNextCursor()
    {
        await SeedAsync(T0.AddMinutes(10), 0.31m);
        await SeedAsync(T0.AddMinutes(20), 0.32m);

        var page = await HandleAsync(limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Handle_FiltersBySymbolIgnoringCase()
    {
        await SeedAsync(T0.AddMinutes(10), 0.31m);
        await SeedAsync(T0.AddMinutes(20), 0.50m, symbol: SupportedSymbol.Silver);

        var page = await HandleAsync(symbol: "xauusd");

        Assert.Equal([0.31m], page.Items.Select(e => e.DeltaPercent));
    }

    [Fact]
    public async Task Handle_FiltersByWindow()
    {
        await SeedAsync(T0.AddMinutes(10), 0.31m, window: "5m");
        await SeedAsync(T0.AddMinutes(20), 0.60m, window: "1h");

        var page = await HandleAsync(window: "1h");

        Assert.Equal([0.60m], page.Items.Select(e => e.DeltaPercent));
    }

    [Fact]
    public async Task Handle_MinPercentMatchesFallsBySize()
    {
        await SeedAsync(T0.AddMinutes(10), 0.29m);   // too small
        await SeedAsync(T0.AddMinutes(20), -0.40m);  // a fall, big enough
        await SeedAsync(T0.AddMinutes(30), 0.30m);   // exactly the minimum: in

        var page = await HandleAsync(minPercent: 0.30m);

        Assert.Equal([0.30m, -0.40m], page.Items.Select(e => e.DeltaPercent));
    }
}
