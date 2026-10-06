using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The poller's cadence decisions and its projection onto <c>price_sources</c>.
/// </summary>
/// <remarks>
/// Against a real Postgres because the projection is half the subject and it writes real rows.
/// The feed is a script, so nothing here touches HTTP or the quota ledger.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PricePollingServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// How long a test waits for a poll it expects. Generous: this bounds a thread-pool hop, not
    /// the clock, which is fake. A test that needs more than this is racing.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a test waits to be satisfied that a poll did <em>not</em> happen. Short, because a
    /// false pass here costs only a slow suite while a false failure costs a flaky one.
    /// </summary>
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly DateTimeOffset Start = PostgresFixture.RecentMinute;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private const string Primary = "api-ninjas";
    private const string Secondary = "goldapi.io";

    public async Task InitializeAsync()
    {
        // Shared database; each test starts from a clean slate for the rows it asserts on. Every
        // tick, not only this class's sources: the poller warms the latest-quote cache from the
        // newest stored tick whatever its source, and a newer leftover row would make the cache
        // drop this test's quote as older. The collection runs one class at a time.
        await using var db = fixture.CreateDbContext();
        await db.PriceTicks.ExecuteDeleteAsync(Ct);
        await db.PriceSources.Where(s => s.Code == Primary || s.Code == Secondary)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LastSuccessAt, (DateTimeOffset?)null)
                .SetProperty(s => s.LastFailureAt, (DateTimeOffset?)null)
                .SetProperty(s => s.LastFailureReason, (string?)null), Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (PricePollingService Poller, FakeTimeProvider Clock, LatestQuoteCache Cache, DeltaEngine Deltas) Build(
        IPriceFeed feed)
    {
        var clock = new FakeTimeProvider(Start);

        var services = new ServiceCollection();
        services.AddSingleton(feed);

        // Transient: the poller opens a scope per poll and a DbContext is not reusable across
        // them once one has failed.
        services.AddTransient(_ => fixture.CreateDbContext(clock));

        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var sources = TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true));
        var polling = Options.Create(new PricePollingOptions { PollInterval = PollInterval });

        // The real cache rather than a stub: the order tests below are about what it holds after
        // the poller has written to it, and a stub would assert the poller called a method.
        var cache = new LatestQuoteCache(scopes, sources, clock, polling, NullLogger<LatestQuoteCache>.Instance);

        var deltas = new DeltaEngine(
            scopes, Options.Create(new DeltaEngineOptions()), clock, NullLogger<DeltaEngine>.Instance);

        var poller = new PricePollingService(
            scopes, sources, clock, polling, cache, deltas, NullLogger<PricePollingService>.Instance);

        return (poller, clock, cache, deltas);
    }

    private static PriceFeedResult Quote(string sourceCode, IReadOnlyList<SourceAttempt> attempts) =>
        new(
            PriceQuote.Normalize(SupportedSymbol.Gold, Start, Start, null, null, 4_000m, sourceCode),
            Primary,
            attempts);

    /// <summary>
    /// Waits until a poll's database side effects are visible.
    /// </summary>
    /// <remarks>
    /// <see cref="ScriptedPriceFeed.WaitForCallAsync"/> completes when the feed is <em>entered</em>,
    /// and the poller writes the tick and the projection after it returns. Asserting straight after
    /// that wait races the save — which is what the first draft of these tests did, and it failed
    /// about as often as it passed. This polls the real clock because it is waiting on a thread-pool
    /// hop and a round trip, neither of which the fake clock drives.
    /// </remarks>
    private async Task WaitUntilAsync(Func<AurumDbContext, Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + CallTimeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var db = fixture.CreateDbContext();
            if (await condition(db))
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    /// <summary>
    /// <see cref="WaitUntilAsync"/> for the in-memory cache: the poller records after the feed
    /// returns, so the same race applies, with no database to poll.
    /// </summary>
    private static async Task<LatestQuoteSnapshot> WaitForCachedAsync(ILatestQuoteCache cache, string symbol)
    {
        var deadline = DateTime.UtcNow + CallTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (cache.Get(symbol) is { } snapshot)
            {
                return snapshot;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"Timed out waiting for a cached {symbol} quote.");
        return null!;
    }

    private static async Task AssertNoFurtherCallAsync(ScriptedPriceFeed feed, int expected)
    {
        var next = feed.WaitForCallAsync(expected + 1);
        var finished = await Task.WhenAny(next, Task.Delay(QuietTimeout, Ct));

        Assert.NotSame(next, finished);
        Assert.Equal(expected, feed.CallCount);
    }

    [Fact]
    public async Task Poller_sleeps_until_the_earliest_reset_when_every_source_is_out_of_quota()
    {
        var soon = Start.AddHours(1);

        var feed = new ScriptedPriceFeed(_ => throw new AllSourcesFailedException(
            SupportedSymbol.Gold,
            [
                new SourceAttempt(Primary, SourceAttemptOutcome.QuotaExhausted, "spent", null, Start.AddDays(30)),
                new SourceAttempt(Secondary, SourceAttemptOutcome.QuotaExhausted, "spent", null, soon),
            ]));

        var (poller, clock, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            // The immediate poll on startup. Awaiting it — rather than advancing and asserting —
            // is what makes everything after this deterministic.
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);

            // One cadence interval is not enough: the poller is asleep until the earliest reset.
            clock.Advance(PollInterval);
            await AssertNoFurtherCallAsync(feed, 1);

            // Still nothing a minute before the reset.
            clock.Advance(TimeSpan.FromMinutes(59) - PollInterval);
            await AssertNoFurtherCallAsync(feed, 1);

            // And a poll at the reset. Earliest, not the +30d one.
            clock.Advance(TimeSpan.FromMinutes(1));
            await feed.WaitForCallAsync(2).WaitAsync(CallTimeout, Ct);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Poller_stays_on_cadence_when_only_some_sources_are_out_of_quota()
    {
        // The regression guard against the AllQuotaExhausted clause being deleted, or against
        // AllQuotaExhausted being weakened to "any". One funded source is serving every poll.
        var feed = new ScriptedPriceFeed(_ => Quote(Secondary,
        [
            new SourceAttempt(Primary, SourceAttemptOutcome.QuotaExhausted, "spent", null, Start.AddDays(30)),
            new SourceAttempt(Secondary, SourceAttemptOutcome.Success),
        ]));

        var (poller, clock, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);

            clock.Advance(PollInterval);
            await feed.WaitForCallAsync(2).WaitAsync(CallTimeout, Ct);

            clock.Advance(PollInterval);
            await feed.WaitForCallAsync(3).WaitAsync(CallTimeout, Ct);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Poller_stays_on_cadence_when_a_circuit_is_open()
    {
        // An open circuit clears on the break duration, which is minutes. Sleeping to a quota
        // reset over it would idle the feed for up to a month for a reason that was never quota.
        var feed = new ScriptedPriceFeed(_ => throw new AllSourcesFailedException(
            SupportedSymbol.Gold,
            [
                new SourceAttempt(Primary, SourceAttemptOutcome.SkippedCircuitOpen),
                new SourceAttempt(Secondary, SourceAttemptOutcome.QuotaExhausted, "spent", null, Start.AddDays(30)),
            ]));

        var (poller, clock, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);

            clock.Advance(PollInterval);
            await feed.WaitForCallAsync(2).WaitAsync(CallTimeout, Ct);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Poller_stays_on_cadence_when_every_source_faults()
    {
        var feed = new ScriptedPriceFeed(_ => throw new AllSourcesFailedException(
            SupportedSymbol.Gold,
            [
                new SourceAttempt(Primary, SourceAttemptOutcome.Faulted, "HTTP 503.", new HttpRequestException()),
                new SourceAttempt(Secondary, SourceAttemptOutcome.Faulted, "HTTP 500.", new HttpRequestException()),
            ]));

        var (poller, clock, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);

            clock.Advance(PollInterval);
            await feed.WaitForCallAsync(2).WaitAsync(CallTimeout, Ct);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task A_successful_poll_persists_the_tick_and_stamps_both_sources()
    {
        var feed = new ScriptedPriceFeed(_ => Quote(Secondary,
        [
            new SourceAttempt(Primary, SourceAttemptOutcome.Faulted, "HTTP 503 Service Unavailable.",
                new HttpRequestException()),
            new SourceAttempt(Secondary, SourceAttemptOutcome.Success),
        ]));

        var (poller, _, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            await WaitUntilAsync(db => db.PriceTicks.AnyAsync(t => t.SourceCode == Secondary, Ct),
                "the tick to be persisted");
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        await using var db = fixture.CreateDbContext();

        var tick = await db.PriceTicks.AsNoTracking()
            .SingleAsync(t => t.SourceCode == Secondary, Ct);
        Assert.Equal(4_000m, tick.Mid);

        var faulted = await db.PriceSources.AsNoTracking().SingleAsync(s => s.Code == Primary, Ct);
        Assert.Equal(Start, faulted.LastFailureAt);
        Assert.Equal("HTTP 503 Service Unavailable.", faulted.LastFailureReason);
        Assert.Null(faulted.LastSuccessAt);

        var served = await db.PriceSources.AsNoTracking().SingleAsync(s => s.Code == Secondary, Ct);
        Assert.Equal(Start, served.LastSuccessAt);
    }

    [Fact]
    public async Task Success_no_longer_clears_the_last_failure_reason()
    {
        await using (var seed = fixture.CreateDbContext())
        {
            var source = await seed.PriceSources.SingleAsync(s => s.Code == Secondary, Ct);
            source.LastFailureAt = Start.AddDays(-1);
            source.LastFailureReason = "yesterday's outage";
            await seed.SaveChangesAsync(Ct);
        }

        var feed = new ScriptedPriceFeed(_ => Quote(Secondary,
            [new SourceAttempt(Secondary, SourceAttemptOutcome.Success)]));

        var (poller, _, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            await WaitUntilAsync(
                db => db.PriceSources.AnyAsync(s => s.Code == Secondary && s.LastSuccessAt != null, Ct),
                "the success stamp to be written");
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        await using var db = fixture.CreateDbContext();
        var served = await db.PriceSources.AsNoTracking().SingleAsync(s => s.Code == Secondary, Ct);

        // It used to null the reason while leaving LastFailureAt set, producing rows that assert a
        // failure at a timestamp with no reason for it. Both persist now; a reader tells current
        // from historical by comparing the two timestamps.
        Assert.Equal("yesterday's outage", served.LastFailureReason);
        Assert.Equal(Start.AddDays(-1), served.LastFailureAt);
        Assert.Equal(Start, served.LastSuccessAt);
    }

    [Fact]
    public async Task A_circuit_skip_does_not_overwrite_the_fault_that_opened_it()
    {
        await using (var seed = fixture.CreateDbContext())
        {
            var source = await seed.PriceSources.SingleAsync(s => s.Code == Primary, Ct);
            source.LastFailureAt = Start.AddMinutes(-10);
            source.LastFailureReason = "HTTP 503 Service Unavailable.";
            await seed.SaveChangesAsync(Ct);
        }

        var feed = new ScriptedPriceFeed(_ => Quote(Secondary,
        [
            new SourceAttempt(Primary, SourceAttemptOutcome.SkippedCircuitOpen),
            new SourceAttempt(Secondary, SourceAttemptOutcome.Success),
        ]));

        var (poller, _, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            await WaitUntilAsync(
                db => db.PriceSources.AnyAsync(s => s.Code == Secondary && s.LastSuccessAt != null, Ct),
                "the poll to finish");
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        await using var db = fixture.CreateDbContext();
        var skipped = await db.PriceSources.AsNoTracking().SingleAsync(s => s.Code == Primary, Ct);

        // The source produced no new evidence, so stamping "circuit open" here would erase the one
        // thing an operator needs: why it opened.
        Assert.Equal("HTTP 503 Service Unavailable.", skipped.LastFailureReason);
        Assert.Equal(Start.AddMinutes(-10), skipped.LastFailureAt);
    }

    [Fact]
    public async Task A_total_outage_is_projected_before_the_exception_propagates()
    {
        var feed = new ScriptedPriceFeed(_ => throw new AllSourcesFailedException(
            SupportedSymbol.Gold,
            [
                new SourceAttempt(Primary, SourceAttemptOutcome.Faulted, "connection refused",
                    new HttpRequestException()),
                new SourceAttempt(Secondary, SourceAttemptOutcome.Faulted, "connection refused",
                    new HttpRequestException()),
            ]));

        var (poller, _, _, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            await WaitUntilAsync(
                db => db.PriceSources.AnyAsync(s => s.Code == Primary && s.LastFailureAt != null, Ct),
                "the outage to be projected");
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        await using var db = fixture.CreateDbContext();

        // Circuit state is authoritative but in-memory. price_sources is the only place an
        // operator can see why the feed is dark, and a total outage is when they go looking.
        var sources = await db.PriceSources.AsNoTracking()
            .Where(s => s.Code == Primary || s.Code == Secondary)
            .ToListAsync(Ct);

        Assert.All(sources, source =>
        {
            Assert.Equal("connection refused", source.LastFailureReason);
            Assert.Equal(Start, source.LastFailureAt);
        });

        Assert.Empty(await db.PriceTicks.AsNoTracking()
            .Where(t => t.SourceCode == Primary || t.SourceCode == Secondary).ToListAsync(Ct));
    }

    /// <summary>
    /// The order rule — memory, then database — as a test. The tick's source is not seeded in
    /// <c>price_sources</c>, so its foreign key fails <c>SaveChangesAsync</c> on every poll.
    /// </summary>
    /// <remarks>
    /// If <c>Record</c> moved below the save, the throw would skip it and the wait would time out.
    /// That is the whole discriminating power of this test, and it was checked by making exactly
    /// that move.
    /// </remarks>
    [Fact]
    public async Task A_poll_writes_the_cache_before_it_persists()
    {
        const string Unseeded = "not-in-price-sources";

        var feed = new ScriptedPriceFeed(_ => Quote(Unseeded,
            [new SourceAttempt(Unseeded, SourceAttemptOutcome.Success)]));

        var (poller, _, cache, _) = Build(feed);
        await poller.StartAsync(Ct);

        LatestQuoteSnapshot cached;
        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            cached = await WaitForCachedAsync(cache, SupportedSymbol.Gold);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        Assert.Equal(Unseeded, cached.Value.Quote.SourceCode);

        // And the database never got it, so the cached price is one the save would have lost.
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.PriceTicks.AnyAsync(t => t.SourceCode == Unseeded, Ct));
    }

    [Fact]
    public async Task A_failed_poll_leaves_the_previous_quote_in_place()
    {
        var feed = new ScriptedPriceFeed(call => call == 0
            ? Quote(Primary, [new SourceAttempt(Primary, SourceAttemptOutcome.Success)])
            : throw new AllSourcesFailedException(
                SupportedSymbol.Gold,
                [
                    new SourceAttempt(Primary, SourceAttemptOutcome.Faulted, "HTTP 503.", new HttpRequestException()),
                    new SourceAttempt(Secondary, SourceAttemptOutcome.Faulted, "HTTP 500.", new HttpRequestException()),
                ]));

        var (poller, clock, cache, _) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);
            await WaitForCachedAsync(cache, SupportedSymbol.Gold);

            clock.Advance(PollInterval);
            await feed.WaitForCallAsync(2).WaitAsync(CallTimeout, Ct);

            // The failed poll's projection is its last side effect, so once it is visible the
            // poll has finished and anything it was going to do to the cache, it has done.
            await WaitUntilAsync(
                db => db.PriceSources.AnyAsync(s => s.Code == Primary && s.LastFailureAt != null, Ct),
                "the failed poll to finish");
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        var held = cache.Get(SupportedSymbol.Gold)!;

        // The first poll's quote, untouched, and older by exactly the interval that went by. A
        // failed poll writes nothing to the cache; price_sources is where its reason lives.
        Assert.Equal(Start, held.Value.Quote.ObservedAt);
        Assert.Equal([Primary], held.Value.AttemptedSources);
        Assert.Equal(PollInterval, held.Age);
    }

    [Fact]
    public async Task A_poll_records_into_the_delta_engine()
    {
        var feed = new ScriptedPriceFeed(_ => Quote(Primary,
            [new SourceAttempt(Primary, SourceAttemptOutcome.Success)]));

        var (poller, _, cache, deltas) = Build(feed);
        await poller.StartAsync(Ct);

        try
        {
            await feed.WaitForCallAsync(1).WaitAsync(CallTimeout, Ct);

            // Recorded on the line after the cache, so once the cache holds the quote the engine
            // has been offered it too.
            await WaitForCachedAsync(cache, SupportedSymbol.Gold);
        }
        finally
        {
            await poller.StopAsync(Ct);
        }

        var snapshot = deltas.GetSnapshot(SupportedSymbol.Gold);

        // The database holds no ticks (InitializeAsync), so warm-up created no history: a snapshot
        // here exists only because the poll recorded. One price, so every window says no answer.
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.DroppedOutOfOrder);
        Assert.All(snapshot.Windows.Values, Assert.Null);
    }
}
