using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// <see cref="DeltaEngine.EnsureWarmAsync"/> against real stored ticks.
/// </summary>
/// <remarks>
/// Apart from <c>DeltaEngineTests</c> so that class stays off Postgres. Same setup as
/// <c>LatestQuoteCacheWarmupTests</c>: seeded source codes, because <c>price_ticks.SourceCode</c>
/// has a foreign key to <c>price_sources</c>, and every tick deleted on entry, because warm-up
/// reads every recent row per symbol whatever its source.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DeltaEngineWarmupTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string GoldApi = "goldapi.io";

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// The real time, truncated to the minute, not a fixed date like the in-memory tests use.
    /// </summary>
    /// <remarks>
    /// <c>price_ticks</c> has TimescaleDB's 30-day retention policy, and its background job measures
    /// "30 days" against the database server's real clock, not the fake one. A fixed date more than
    /// 30 days ago gets its rows dropped whenever that job happens to run between the seed and the
    /// warm-up read, so the warm-up finds nothing and the test fails on some runs only.
    /// </remarks>
    private static readonly DateTimeOffset Now = TruncateToMinute(DateTimeOffset.UtcNow);

    private static DateTimeOffset TruncateToMinute(DateTimeOffset time) =>
        new(time.Ticks - (time.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.PriceTicks.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (DeltaEngine Engine, CountingScopeFactory Scopes) Build()
    {
        var clock = new FakeTimeProvider(Now);

        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext(clock));

        var scopes = new CountingScopeFactory(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var engine = new DeltaEngine(
            scopes, Options.Create(new DeltaEngineOptions()), clock, NullLogger<DeltaEngine>.Instance);

        return (engine, scopes);
    }

    private async Task SeedAsync(params (TimeSpan Ago, decimal Mid)[] ticks)
    {
        await using var db = fixture.CreateDbContext();

        foreach (var (ago, mid) in ticks)
        {
            db.PriceTicks.Add(new PriceTick
            {
                Symbol = SupportedSymbol.Gold,
                ObservedAt = Now - ago,
                ReceivedAt = Now - ago,
                Mid = mid,
                SourceCode = GoldApi,
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// The 1d window's starting price is the last one at or before "now minus one day", which here
    /// is 25 hours old. A warm-up that read only <c>ObservedAt &gt;= now - 1 day</c>, as the spec
    /// first said, would skip it, and the 1d window would say no answer for a day after every
    /// restart. The 40-hour tick is outside the 36-hour lookback and must not be the start.
    /// </summary>
    [Fact]
    public async Task Warm_up_reaches_back_far_enough_for_the_one_day_start()
    {
        await SeedAsync(
            (TimeSpan.FromHours(40), 3_000m),
            (TimeSpan.FromHours(25), 4_000m),
            (TimeSpan.FromHours(12), 4_020m),
            (TimeSpan.FromMinutes(1), 4_040m));

        var (engine, _) = Build();
        await engine.EnsureWarmAsync(Ct);

        var day = engine.GetSnapshot(SupportedSymbol.Gold)?[DeltaWindow.OneDay];

        Assert.NotNull(day);
        Assert.Equal(Now - TimeSpan.FromHours(25), day.Start.ObservedAt);
        Assert.Equal(1m, day.DeltaPercent);

        // Rows were flipped into time order: all three loaded ones sit between start and end.
        Assert.Equal(3, day.SampleCount);
    }

    [Fact]
    public async Task Warm_up_twice_does_not_count_drops()
    {
        await SeedAsync((TimeSpan.FromHours(2), 4_000m), (TimeSpan.FromHours(1), 4_010m));

        var (engine, scopes) = Build();
        await engine.EnsureWarmAsync(Ct);

        // A live poll lands between two warm-ups, then two more callers arrive at once.
        engine.Record(new PriceQuote(SupportedSymbol.Gold, Now, Now, null, null, 4_020m, GoldApi));
        await Task.WhenAll(engine.EnsureWarmAsync(Ct), engine.EnsureWarmAsync(Ct));

        var snapshot = engine.GetSnapshot(SupportedSymbol.Gold)!;

        // One load shared by all three callers. A second would offer two rows older than the live
        // price, and if they went through Record they would count as dropped.
        Assert.Equal(1, scopes.Created);
        Assert.Equal(0, snapshot.DroppedOutOfOrder);
        // And the history is the one load plus the live price, not the live price alone.
        Assert.Equal(4_010m, snapshot[DeltaWindow.OneHour]?.Start.Mid);
    }

    [Fact]
    public async Task Warm_up_with_no_ticks_leaves_no_history()
    {
        var (engine, _) = Build();

        await engine.EnsureWarmAsync(Ct);

        // Null, not a snapshot of six nulls: this process has never held a price for either symbol.
        Assert.Null(engine.GetSnapshot(SupportedSymbol.Gold));
        Assert.Null(engine.GetSnapshot(SupportedSymbol.Silver));
    }
}
