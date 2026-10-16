using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Handlers.Queries;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Handlers;

/// <remarks>
/// Real Postgres: the subject is the SQL the keyset filter becomes. Ticks are dated from
/// <see cref="PostgresFixture.RecentMinute"/> so the retention job can't drop them mid-test.
/// Mids are row labels; the assertions compare them in order.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class GetPriceHistoryQueryHandlerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string GoldApi = GoldApiIoSource.SourceCode;
    private const string ApiNinjas = ApiNinjasSource.SourceCode;

    private static readonly DateTimeOffset Now = PostgresFixture.RecentMinute;

    private static readonly DateTimeOffset From = Now.AddHours(-1);

    public async Task InitializeAsync()
    {
        // Every tick, not only this class's: the handler reads any row in range whatever its source.
        await using var db = fixture.CreateDbContext();
        await db.PriceTicks.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CursorPage<PriceTickDto>> HandleAsync(
        int? limit = null, string? cursor = null, string symbol = SupportedSymbol.Gold,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var clock = new FakeTimeProvider(Now);
        await using var db = fixture.CreateDbContext(clock);

        return await new GetPriceHistoryQueryHandler(db, clock).Handle(
            new GetPriceHistoryQuery(symbol, from ?? From, to, limit, cursor), CancellationToken.None);
    }

    /// <summary>Follows NextCursor to the end. Capped so a cursor that never advances fails instead of hanging.</summary>
    private async Task<List<decimal>> WalkAllPagesAsync(int limit)
    {
        var mids = new List<decimal>();
        string? cursor = null;

        for (var pages = 0; pages < 20; pages++)
        {
            var page = await HandleAsync(limit, cursor);
            mids.AddRange(page.Items.Select(t => t.Mid));

            if ((cursor = page.NextCursor) is null)
            {
                return mids;
            }
        }

        Assert.Fail("Still returning a NextCursor after 20 pages.");
        return mids;
    }

    [Fact]
    public async Task Handle_FirstPage_ReturnsOldestFirstUpToLimitWithACursor()
    {
        // Inserted out of order, so insertion order and time order disagree.
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, From.AddMinutes(30), 103m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(10), 101m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(20), 102m, ApiNinjas));

        var page = await HandleAsync(limit: 2);

        Assert.Equal([101m, 102m], page.Items.Select(t => t.Mid));
        Assert.NotNull(page.NextCursor);

        var second = page.Items[1];
        Assert.Equal(From.AddMinutes(20), second.ObservedAt);
        Assert.Equal(From.AddMinutes(20), second.ReceivedAt);
        Assert.Equal(ApiNinjas, second.SourceCode);
    }

    [Fact]
    public async Task Handle_NextPageViaCursor_ContinuesAfterTheLastRowAndEnds()
    {
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, From.AddMinutes(10), 101m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(20), 102m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(30), 103m, GoldApi));

        var first = await HandleAsync(limit: 2);
        var second = await HandleAsync(limit: 2, cursor: first.NextCursor);

        Assert.Equal([103m], second.Items.Select(t => t.Mid));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Handle_TwoRowsWithTheSameTimeSplitAcrossAPage_NeitherRepeatsNorSkips()
    {
        var shared = From.AddMinutes(20);

        // Separate saves so the Ids are certainly in this order: 102 before 103.
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, From.AddMinutes(10), 101m, GoldApi));
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, shared, 102m, GoldApi));
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, shared, 103m, ApiNinjas));
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, From.AddMinutes(30), 104m, GoldApi));

        // Page one ends on 102; page two must start on 103, which has the same ObservedAt.
        // A cursor on time alone skips 103; ">=" instead of ">" repeats 102.
        Assert.Equal([101m, 102m, 103m, 104m], await WalkAllPagesAsync(limit: 2));
    }

    [Fact]
    public async Task Handle_ExactlyLimitRowsLeft_HasNoNextCursor()
    {
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, From.AddMinutes(10), 101m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(20), 102m, GoldApi));

        var page = await HandleAsync(limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor); // a cursor here sends the caller to an empty page
    }

    [Fact]
    public async Task Handle_WithNoRowsInRange_ReturnsAnEmptyLastPage()
    {
        var page = await HandleAsync(limit: 2);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Handle_WithNoLimit_UsesTheDefaultPageSize()
    {
        await fixture.SeedTicksAsync(Enumerable.Range(1, PaginationQuery.DefaultPageSize + 1)
            .Select(i => (SupportedSymbol.Gold, From.AddSeconds(i), (decimal)i, GoldApi))
            .ToArray());

        var page = await HandleAsync(limit: null);

        Assert.Equal(PaginationQuery.DefaultPageSize, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Handle_FiltersBySymbolIgnoringCase()
    {
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, From.AddMinutes(10), 101m, GoldApi),
            (SupportedSymbol.Silver, From.AddMinutes(15), 31m, GoldApi),
            (SupportedSymbol.Gold, From.AddMinutes(20), 102m, GoldApi));

        var page = await HandleAsync(symbol: "xauusd");

        Assert.Equal([101m, 102m], page.Items.Select(t => t.Mid));
    }

    [Fact]
    public async Task Handle_IncludesFromAndExcludesTo()
    {
        var to = From.AddMinutes(30);
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, From.AddTicks(-10), 100m, GoldApi), // before from (1 µs; Postgres keeps µs)
            (SupportedSymbol.Gold, From, 101m, GoldApi),               // exactly from: in
            (SupportedSymbol.Gold, to.AddTicks(-10), 102m, GoldApi),   // just before to: in
            (SupportedSymbol.Gold, to, 103m, GoldApi));                // exactly to: out

        var page = await HandleAsync(to: to);

        Assert.Equal([101m, 102m], page.Items.Select(t => t.Mid));
    }

    [Fact]
    public async Task Handle_WithNoTo_StopsAtTheInjectedClock()
    {
        // Ahead of the fake clock. Visible if the handler reads the wall clock instead, or no bound.
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, Now.AddMinutes(-1), 101m, GoldApi),
            (SupportedSymbol.Gold, Now, 102m, GoldApi),
            (SupportedSymbol.Gold, Now.AddMinutes(1), 103m, GoldApi));

        var page = await HandleAsync(to: null);

        Assert.Equal([101m], page.Items.Select(t => t.Mid));
    }
}
