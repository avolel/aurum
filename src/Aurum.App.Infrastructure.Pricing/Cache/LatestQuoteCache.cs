using System.Collections.Concurrent;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <remarks>
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/>, not <c>IMemoryCache</c>: eviction would
/// make an old price look like no price (D-16). No lock: entries are immutable and swapped whole.
/// </remarks>
internal sealed class LatestQuoteCache(
    IServiceScopeFactory scopeFactory,
    IOptions<PriceSourcesOptions> sources,
    TimeProvider clock,
    IOptions<PricePollingOptions> polling,
    ILogger<LatestQuoteCache> logger) : ILatestQuoteCache
{
    // IgnoreCase to match PriceSourcesOptions.Sources and PriceFeedResult.UsedFallback.
    private readonly ConcurrentDictionary<string, LatestQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);

    // Twice the interval by default: one late poll is normal, two is a fault.
    private readonly TimeSpan _staleAfter = polling.Value.StaleAfter ?? polling.Value.PollInterval * 2;

    public void Record(PriceFeedResult result)
    {
        // The one place Attempts (and their Exceptions) are dropped.
        var incoming = new LatestQuote(result.Quote, result.UsedFallback, result.AttemptedSources);

        // Compare inside the delegate; TryGetValue then set would reopen the race. The delegate
        // can run more than once, so it must stay side-effect free.
        var held = _quotes.AddOrUpdate(
            result.Quote.Symbol,
            incoming,
            (_, current) => incoming.Quote.ObservedAt > current.Quote.ObservedAt ? incoming : current);

        if (!ReferenceEquals(held, incoming))
        {
            // Newer only, same rule as the delta engine (D-16).
            logger.LogDebug(
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

        // From ObservedAt, or a frozen provider would look fresh forever (D-16).
        var age = clock.GetUtcNow() - held.Quote.ObservedAt;

        return new LatestQuoteSnapshot(held, age, IsStale: age > _staleAfter);
    }

    /// <remarks>
    /// Goes through <see cref="Record"/>, so it never replaces a newer live quote. A reloaded quote
    /// has empty <c>AttemptedSources</c> and an <c>IsFallback</c> judged by the current config (D-16).
    /// </remarks>
    public async Task EnsureWarmAsync(CancellationToken ct)
    {
        // PriceSourcesOptionsValidator guarantees at least one enabled source at boot.
        var primary = sources.Value.EnabledInFailoverOrder()[0].SourceCode;

        // A scope per warm-up: this singleton must not hold a DbContext.
        using var scope = scopeFactory.CreateScope();
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
                logger.LogInformation("No stored {Symbol} tick to warm the latest-quote cache from.", symbol);
                continue;
            }

            // Not Normalize: a since-tightened band must not fail startup over stored data (D-16).
            var quote = new PriceQuote(
                tick.Symbol, tick.ObservedAt, tick.ReceivedAt, tick.Bid, tick.Ask, tick.Mid, tick.SourceCode);

            Record(new PriceFeedResult(quote, primary, Attempts: []));

            // Information: until item 8 this is the only sign a restart picked up a price.
            logger.LogInformation(
                "Warmed {Symbol} from stored tick: {SourceCode}, observed {ObservedAt:o}, {Age} old.",
                symbol, tick.SourceCode, tick.ObservedAt, clock.GetUtcNow() - tick.ObservedAt);
        }
    }
}
