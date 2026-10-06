using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Aurum.App.Infrastructure.Pricing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// <see cref="LatestQuoteCache.EnsureWarmAsync"/> against real stored ticks.
/// </summary>
/// <remarks>
/// Apart from <c>LatestQuoteCacheTests</c> so that class stays off Postgres. The source codes are
/// seeded ones because <c>price_ticks.SourceCode</c> has a foreign key to <c>price_sources</c>.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LatestQuoteCacheWarmupTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string GoldApi = "goldapi.io";
    private const string ApiNinjas = "api-ninjas";

    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        // Every tick, not only this class's: warm-up reads the newest row per symbol whatever its
        // source, so a row left by another class would be what it found. The collection runs its
        // classes one at a time and each cleans up on entry, so this cannot pull rows from under
        // a running test.
        await using var db = fixture.CreateDbContext();
        await db.PriceTicks.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (LatestQuoteCache Cache, FakeTimeProvider Clock) Build(string primary)
    {
        var clock = new FakeTimeProvider(Now);

        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext(clock));

        var cache = new LatestQuoteCache(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TestPriceSources.ForChain(
                (primary, 1, true),
                (primary == GoldApi ? ApiNinjas : GoldApi, 2, true)),
            clock,
            Options.Create(new PricePollingOptions { PollInterval = TimeSpan.FromMinutes(15) }),
            NullLogger<LatestQuoteCache>.Instance);

        return (cache, clock);
    }

    [Fact]
    public async Task Warmup_seeds_the_newest_tick_per_symbol()
    {
        // Inserted out of order, so a warm-up that took the first or last row inserted would pick
        // the wrong one. Silver's only tick is older than every gold tick, so a query that forgot
        // the symbol filter would hand gold's newest to silver.
        await fixture.SeedTicksAsync(
            (SupportedSymbol.Gold, Now.AddHours(-3), 4_001m, GoldApi),
            (SupportedSymbol.Gold, Now.AddHours(-1), 4_003m, GoldApi),
            (SupportedSymbol.Gold, Now.AddHours(-2), 4_002m, GoldApi),
            (SupportedSymbol.Silver, Now.AddHours(-5), 50m, GoldApi));

        var (cache, _) = Build(primary: GoldApi);
        await cache.EnsureWarmAsync(Ct);

        var gold = cache.Get(SupportedSymbol.Gold);
        Assert.NotNull(gold);
        Assert.Equal(4_003m, gold.Value.Quote.Mid);
        Assert.Equal(TimeSpan.FromHours(1), gold.Age);

        var silver = cache.Get(SupportedSymbol.Silver);
        Assert.NotNull(silver);
        Assert.Equal(50m, silver.Value.Quote.Mid);
    }

    [Fact]
    public async Task Warmup_is_idempotent()
    {
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, Now.AddHours(-1), 4_000m, GoldApi));

        var (cache, _) = Build(primary: GoldApi);
        await cache.EnsureWarmAsync(Ct);

        // A live poll lands between two warm-ups.
        var live = new PriceQuote(SupportedSymbol.Gold, Now, Now, null, null, 4_100m, GoldApi);
        cache.Record(new PriceFeedResult(live, GoldApi, [new SourceAttempt(GoldApi, SourceAttemptOutcome.Success)]));

        await cache.EnsureWarmAsync(Ct);

        // The stored tick is older, so the second warm-up offers it and Record drops it.
        var held = cache.Get(SupportedSymbol.Gold)!.Value;
        Assert.Same(live, held.Quote);
        Assert.Equal([GoldApi], held.AttemptedSources);
    }

    [Fact]
    public async Task Warmup_with_no_ticks_leaves_the_cache_empty()
    {
        var (cache, _) = Build(primary: GoldApi);

        await cache.EnsureWarmAsync(Ct);

        // Null, not an empty snapshot: an empty database is "no price", not "a price of zero".
        Assert.Null(cache.Get(SupportedSymbol.Gold));
        Assert.Null(cache.Get(SupportedSymbol.Silver));
    }

    [Theory]
    [InlineData(GoldApi, true)]     // tick from api-ninjas, primary is goldapi.io: a fallback
    [InlineData(ApiNinjas, false)]  // same tick, primary reordered across the "restart": not one
    public async Task Warmup_derives_IsFallback_from_the_configured_primary(string primary, bool isFallback)
    {
        await fixture.SeedTicksAsync((SupportedSymbol.Gold, Now.AddMinutes(-10), 4_000m, ApiNinjas));

        var (cache, _) = Build(primary);
        await cache.EnsureWarmAsync(Ct);

        var held = cache.Get(SupportedSymbol.Gold)!.Value;

        // Same stored row both times; only the configuration differs. That the flag follows the
        // configuration, not the row, is the behaviour D-16 says to expect.
        Assert.Equal(isFallback, held.IsFallback);

        // Not observed by this process, so not invented.
        Assert.Empty(held.AttemptedSources);
    }
}
