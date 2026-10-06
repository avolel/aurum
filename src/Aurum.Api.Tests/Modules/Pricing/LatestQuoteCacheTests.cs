using System.Reflection;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The latest-quote cache in memory. No container, no HTTP, sub-second.
/// </summary>
/// <remarks>
/// No container: age is the difference of two <see cref="TimeProvider"/> reads, so moving a fake
/// clock is the whole setup. Warm-up needs Postgres and lives in its own class.
/// </remarks>
public class LatestQuoteCacheTests
{
    private const string Primary = GoldApiIoSource.SourceCode;
    private const string Backup = MetalPriceApiSource.SourceCode;

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private static (LatestQuoteCache Cache, FakeTimeProvider Clock) Build(TimeSpan? staleAfter = null)
    {
        var clock = new FakeTimeProvider(Start);
        var polling = Options.Create(new PricePollingOptions
        {
            PollInterval = PollInterval,
            StaleAfter = staleAfter,
        });

        // Warm-up is the only path that opens a scope, and it lives in LatestQuoteCacheWarmupTests,
        // so an empty container is enough here and keeps this class off Postgres.
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var cache = new LatestQuoteCache(
            scopes,
            TestPriceSources.ForChain((Primary, 1, true), (Backup, 2, true)),
            clock,
            polling,
            NullLogger<LatestQuoteCache>.Instance);

        return (cache, clock);
    }

    private static PriceQuote Quote(
        DateTimeOffset observedAt,
        decimal mid = 4_000m,
        string sourceCode = Primary,
        DateTimeOffset? receivedAt = null) =>
        new(SupportedSymbol.Gold, observedAt, receivedAt ?? observedAt, null, null, mid, sourceCode);

    private static PriceFeedResult Success(PriceQuote quote) =>
        new(quote, Primary, [new SourceAttempt(quote.SourceCode, SourceAttemptOutcome.Success)]);

    [Fact]
    public void Get_before_any_poll_returns_null()
    {
        var (cache, _) = Build();

        Assert.Null(cache.Get(SupportedSymbol.Gold));
    }

    /// <summary>
    /// Decision 3 as a test. What Age is measured from is a one-line choice; without this,
    /// flipping it to ReceivedAt passes everything and blinds the endpoint to a frozen provider.
    /// </summary>
    [Fact]
    public void Age_is_measured_from_ObservedAt_not_ReceivedAt()
    {
        var (cache, _) = Build();

        // The provider says the price is an hour old; the app only just received it.
        cache.Record(Success(Quote(observedAt: Start.AddHours(-1), receivedAt: Start)));

        var snapshot = cache.Get(SupportedSymbol.Gold);

        Assert.NotNull(snapshot);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Age);
    }

    [Fact]
    public void A_quote_older_than_the_held_one_is_dropped()
    {
        var (cache, _) = Build();

        var newer = Quote(Start, mid: 4_100m, sourceCode: Primary);
        cache.Record(Success(newer));

        // A lagging backup: later poll, earlier price.
        cache.Record(Success(Quote(Start.AddMinutes(-5), mid: 3_900m, sourceCode: Backup)));

        // The held quote is unchanged — not merely "no exception was thrown".
        Assert.Same(newer, cache.Get(SupportedSymbol.Gold)!.Value.Quote);
    }

    /// <summary>
    /// Equal is not newer. Two sources reporting the same instant keep the first, so the
    /// displayed source does not flicker between polls that agree on the time.
    /// </summary>
    [Fact]
    public void A_quote_with_the_same_ObservedAt_is_dropped()
    {
        var (cache, _) = Build();

        var first = Quote(Start, sourceCode: Primary);
        cache.Record(Success(first));
        cache.Record(Success(Quote(Start, sourceCode: Backup)));

        Assert.Same(first, cache.Get(SupportedSymbol.Gold)!.Value.Quote);
    }

    [Fact]
    public void IsStale_flips_at_twice_the_poll_interval()
    {
        var (cache, clock) = Build();
        cache.Record(Success(Quote(Start)));

        // Exactly at the threshold is not stale: the rule is Age > StaleAfter.
        clock.Advance(PollInterval * 2);
        Assert.False(cache.Get(SupportedSymbol.Gold)!.IsStale);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(cache.Get(SupportedSymbol.Gold)!.IsStale);
    }

    [Fact]
    public void An_explicit_StaleAfter_overrides_the_derived_default()
    {
        var (cache, clock) = Build(staleAfter: TimeSpan.FromHours(1));
        cache.Record(Success(Quote(Start)));

        // Well past the derived 30 minutes, still inside the configured hour.
        clock.Advance(TimeSpan.FromMinutes(45));
        Assert.False(cache.Get(SupportedSymbol.Gold)!.IsStale);

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True(cache.Get(SupportedSymbol.Gold)!.IsStale);
    }

    [Fact]
    public void A_fallback_poll_is_recorded_as_a_fallback()
    {
        var (cache, _) = Build();

        var result = new PriceFeedResult(
            Quote(Start, sourceCode: Backup),
            Primary,
            [
                new SourceAttempt(Primary, SourceAttemptOutcome.Faulted, "timeout", new TimeoutException()),
                new SourceAttempt(Backup, SourceAttemptOutcome.Success),
            ]);

        cache.Record(result);

        var held = cache.Get(SupportedSymbol.Gold)!.Value;
        Assert.True(held.IsFallback);
        Assert.Equal([Primary, Backup], held.AttemptedSources);
    }

    [Fact]
    public void Lookup_ignores_symbol_case()
    {
        var (cache, _) = Build();
        cache.Record(Success(Quote(Start)));

        Assert.NotNull(cache.Get(SupportedSymbol.Gold.ToLowerInvariant()));
    }

    /// <summary>
    /// Pins a decision rather than finding a bug, like <c>Transport_failure_still_spends_the_lease</c>.
    /// It passes today by construction; it is the only thing that stops someone adding
    /// <c>Attempts</c> to <see cref="LatestQuote"/> for convenience and pinning a stack trace per
    /// symbol for as long as the price stays current.
    /// </summary>
    [Fact]
    public void A_cached_quote_holds_no_exception_reference()
    {
        var offending = typeof(LatestQuote)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => CanHoldAnException(p.PropertyType))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(offending);
    }

    /// <summary>
    /// The type itself, or any generic argument (so <c>IReadOnlyList&lt;SourceAttempt&gt;</c> is
    /// caught too), is an exception, a <see cref="SourceAttempt"/>, or <see cref="object"/>.
    /// </summary>
    private static bool CanHoldAnException(Type type) =>
        typeof(Exception).IsAssignableFrom(type)
        || type == typeof(SourceAttempt)
        || type == typeof(object)
        || type.GetGenericArguments().Any(CanHoldAnException)
        || (type.IsArray && CanHoldAnException(type.GetElementType()!));

    /// <summary>
    /// Many writers offering different ObservedAt at the same instant. After every round the held
    /// entry must be that round's newest, with every field from that one record.
    /// </summary>
    /// <remarks>
    /// Rounds checked one at a time: a single long run passed every time against a broken
    /// check-then-set, because only the last few writes decide the final state. Dedicated threads,
    /// not <c>Task.Run</c>: pool threads arrive at the barrier staggered.
    /// </remarks>
    [Fact]
    public void Concurrent_records_leave_a_consistent_entry()
    {
        const int Writers = 8;
        const int Rounds = 2_000;

        var (cache, _) = Build();
        var failures = new List<string>();

        // Two phases per round: release the writers, then wait for all of them. The post-phase
        // action runs on one thread while every writer is parked, so the check races nothing.
        using var gate = new Barrier(Writers, b =>
        {
            if (b.CurrentPhaseNumber % 2 == 0)
            {
                return;
            }

            var round = (int)(b.CurrentPhaseNumber / 2);
            var newest = round * Writers + Writers - 1;
            var held = cache.Get(SupportedSymbol.Gold)!.Value;

            var consistent =
                held.Quote.ObservedAt == Start.AddSeconds(newest)
                && held.Quote.Mid == newest
                && held.Quote.SourceCode == $"source-{newest}"
                && held.AttemptedSources.SequenceEqual([$"source-{newest}"]);

            if (!consistent)
            {
                failures.Add($"round {round}: expected {newest}, held {held.Quote.Mid}");
            }
        });

        var threads = Enumerable.Range(0, Writers).Select(w => new Thread(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                gate.SignalAndWait();

                var index = round * Writers + w;
                cache.Record(Success(Quote(
                    Start.AddSeconds(index),
                    mid: index,
                    sourceCode: $"source-{index}")));

                gate.SignalAndWait();
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(failures);
    }
}
