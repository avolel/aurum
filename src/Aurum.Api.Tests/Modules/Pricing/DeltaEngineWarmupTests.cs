using Aurum.Api.Tests.Infrastructure;
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
/// <c>LatestQuoteCacheWarmupTests</c>: seeded source codes and every tick deleted on entry.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DeltaEngineWarmupTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string GoldApi = GoldApiIoSource.SourceCode;

    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset Now = PostgresFixture.RecentMinute;

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

    /// <summary>
    /// The 1d start is the last price at or before "now minus one day", here 25 hours old, so a
    /// one-day lookback would leave the window dark for a day after every restart. The 40-hour
    /// tick is outside the 36-hour lookback and must not be the start.
    /// </summary>
    [Fact]
    public async Task Warm_up_reaches_back_far_enough_for_the_one_day_start()
    {
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, Now.AddHours(-40), 3_000m, GoldApi),
            (SupportedSymbol.Gold, Now.AddHours(-25), 4_000m, GoldApi),
            (SupportedSymbol.Gold, Now.AddHours(-12), 4_020m, GoldApi),
            (SupportedSymbol.Gold, Now.AddMinutes(-1), 4_040m, GoldApi));

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
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, Now.AddHours(-2), 4_000m, GoldApi),
            (SupportedSymbol.Gold, Now.AddHours(-1), 4_010m, GoldApi));

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
