using System.Collections.Concurrent;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <remarks>
/// <para>A plain <see cref="ConcurrentDictionary{TKey,TValue}"/>, not <c>IMemoryCache</c> (D-16).
/// <c>IMemoryCache</c> evicts on a timer, which is exactly wrong when polls can be hours apart: an
/// evicted entry is indistinguishable from "never had a price", when the honest answer is "here is
/// the price, and it is eight hours old". Freshness computed on read is the requirement; freshness
/// enforced by eviction can only delete, and the age a person needs to see is what it
/// destroys.</para>
///
/// <para>No lock. Each entry is an immutable record swapped in whole, so a reader holds a
/// reference to a finished record and cannot observe a half-written one.</para>
///
/// <para>Singleton, alongside <see cref="SourceCircuitStore"/>. It cannot live on
/// <see cref="FailoverPriceFeed"/>, which is scoped, so each scope would start empty.</para>
/// </remarks>
internal sealed class LatestQuoteCache : ILatestQuoteCache
{
    // IgnoreCase because PriceSourcesOptions.Sources and PriceFeedResult.UsedFallback already are.
    // An ordinal key here would be the one link in the chain where "xauusd" misses "XAUUSD".
    private readonly ConcurrentDictionary<string, LatestQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<PriceSourcesOptions> _sources;
    private readonly TimeProvider _clock;
    private readonly ILogger<LatestQuoteCache> _logger;
    private readonly TimeSpan _staleAfter;

    public LatestQuoteCache(
        IServiceScopeFactory scopeFactory,
        IOptions<PriceSourcesOptions> sources,
        TimeProvider clock,
        IOptions<PricePollingOptions> polling,
        ILogger<LatestQuoteCache> logger)
    {
        _scopeFactory = scopeFactory;
        _sources = sources;
        _clock = clock;
        _logger = logger;

        // Twice the interval by default: one missed poll is normal jitter and must not flag, two
        // is a fault. Derived once here so the threshold cannot drift from the cadence it tracks.
        _staleAfter = polling.Value.StaleAfter ?? polling.Value.PollInterval * 2;
    }

    public void Record(PriceFeedResult result)
    {
        // Projected here, in one place, so dropping Attempts (and the Exceptions they hold) cannot
        // be forgotten by a second caller building its own LatestQuote.
        var incoming = new LatestQuote(result.Quote, result.UsedFallback, result.AttemptedSources);

        // The comparison lives inside AddOrUpdate's update delegate, not in a TryGetValue followed
        // by an indexer write: that would reopen the check-then-set gap, and a concurrent older
        // write could land after a newer one. The delegate may run more than once under
        // contention, so it stays side-effect free and the log is decided afterwards by identity.
        var held = _quotes.AddOrUpdate(
            result.Quote.Symbol,
            incoming,
            (_, current) => incoming.Quote.ObservedAt > current.Quote.ObservedAt ? incoming : current);

        if (!ReferenceEquals(held, incoming))
        {
            // Newer-only (D-16): a lagging fallback must not wind the displayed price backwards, and
            // item 5's ring buffer applies the same rule, so accepting what it drops would put this
            // price out of step with the chart beside it. A stuck provider therefore leaves Age
            // climbing — that is the correct reading, not a bug.
            _logger.LogDebug(
                "Dropped {Symbol} quote from {SourceCode} observed at {ObservedAt:o}: not newer than the held quote from {HeldSourceCode} observed at {HeldObservedAt:o}.",
                result.Quote.Symbol,
                result.Quote.SourceCode,
                result.Quote.ObservedAt,
                held.Quote.SourceCode,
                held.Quote.ObservedAt);
        }
    }

    public LatestQuoteSnapshot? Get(string symbol)
    {
        if (!_quotes.TryGetValue(symbol, out var held))
        {
            return null;
        }

        // From ObservedAt, not ReceivedAt. A provider that keeps answering 200 with a frozen
        // ObservedAt is the failure this number exists to expose; measured from ReceivedAt it would
        // look fresh forever. The injected clock, never the database's now(), same as the governor.
        var age = _clock.GetUtcNow() - held.Quote.ObservedAt;

        return new LatestQuoteSnapshot(held, age, IsStale: age > _staleAfter);
    }

    /// <remarks>
    /// <para>Safe to repeat: every tick goes through <see cref="Record"/>, so a second warm-up — or
    /// one racing the first poll — cannot replace a newer live quote with the stored one.</para>
    ///
    /// <para>A reloaded quote has two fields this process did not observe, and both are stated
    /// rather than invented:</para>
    /// <list type="bullet">
    /// <item><c>AttemptedSources</c> is empty. Empty means "not observed by this process", not "no
    /// source was called"; item 8 must not render it as a failed chain.</item>
    /// <item><c>IsFallback</c> is derived by comparing the tick's source with the primary in the
    /// <em>current</em> configuration, not remembered. Reordering sources across a restart can
    /// therefore flip it on a price that has not changed.</item>
    /// </list>
    /// </remarks>
    public async Task EnsureWarmAsync(CancellationToken ct)
    {
        var enabled = _sources.Value.EnabledInFailoverOrder();
        if (enabled.Count == 0)
        {
            // PriceSourcesOptionsValidator refuses this at boot, and the poller returns before
            // calling here. Without a primary there is nothing honest to derive IsFallback from.
            return;
        }

        var primary = enabled[0].SourceCode;

        // A scope per warm-up, never a held DbContext: this object lives for the whole process,
        // and a captured context would be shared by every later caller (same reason as QuotaHandler).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        foreach (var symbol in SupportedSymbol.All)
        {
            // Served by the (Symbol, ObservedAt DESC) index; one row per symbol.
            var tick = await db.PriceTicks.AsNoTracking()
                .Where(t => t.Symbol == symbol)
                .OrderByDescending(t => t.ObservedAt)
                .FirstOrDefaultAsync(ct);

            if (tick is null)
            {
                _logger.LogInformation("No stored {Symbol} tick to warm the latest-quote cache from.", symbol);
                continue;
            }

            // The constructor, not PriceQuote.Normalize: this tick already passed Normalize when it
            // was fetched, and re-running the plausibility band on stored data could throw at boot
            // over a band that has since been tightened.
            var quote = new PriceQuote(
                tick.Symbol, tick.ObservedAt, tick.ReceivedAt, tick.Bid, tick.Ask, tick.Mid, tick.SourceCode);

            // No attempts: see the remarks. PriceFeedResult derives UsedFallback from the primary.
            Record(new PriceFeedResult(quote, primary, Attempts: []));

            // Information, not Debug: until item 8 serves the price, this line is the only way to
            // see that a restart picked up the last known price.
            _logger.LogInformation(
                "Warmed {Symbol} from stored tick: {SourceCode}, observed {ObservedAt:o}, {Age} old.",
                symbol, tick.SourceCode, tick.ObservedAt, _clock.GetUtcNow() - tick.ObservedAt);
        }
    }
}
