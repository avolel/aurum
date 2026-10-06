using Aurum.App.SharedKernel.Constants;
using Aurum.App.Infrastructure.Data.Entities.Pricing;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// The canonical tick shape from FR-1.2, before persistence.
/// </summary>
/// <param name="Symbol">e.g. <c>XAUUSD</c>.</param>
/// <param name="ObservedAt">Provider-reported validity time, in UTC.</param>
/// <param name="ReceivedAt">When the app read the response.</param>
/// <param name="Bid">Null where the provider does not quote two-sided.</param>
/// <param name="Ask">Null where the provider does not quote two-sided.</param>
/// <param name="Mid">Always populated — see <see cref="Normalize"/>.</param>
/// <param name="SourceCode">Which <see cref="IPriceSource"/> produced this.</param>
public sealed record PriceQuote(
    string Symbol,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    decimal? Bid,
    decimal? Ask,
    decimal Mid,
    string SourceCode)
{
    /// <summary>
    /// Per-symbol plausibility band for a mid, in the quote currency per troy ounce.
    /// </summary>
    /// <remarks>
    /// Catches an inverted or wrongly scaled rate, which would otherwise round to 0.0002 and save
    /// without error. Wide on purpose: it checks units, not market moves.
    /// </remarks>
    private static readonly Dictionary<string, (decimal Min, decimal Max)> PlausibleMid =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SupportedSymbol.Gold] = (100m, 50_000m),
            [SupportedSymbol.Silver] = (1m, 5_000m),
        };

    /// <summary>
    /// Builds a quote, deriving the mid only when the provider gives none. The provider's own mid
    /// may be a last-traded price, and replacing it would change what the chart means.
    /// </summary>
    public static PriceQuote Normalize(
        string symbol,
        DateTimeOffset observedAt,
        DateTimeOffset receivedAt,
        decimal? bid,
        decimal? ask,
        decimal? providerMid,
        string sourceCode)
    {
        var mid = providerMid
            ?? (bid.HasValue && ask.HasValue ? (bid.Value + ask.Value) / 2m : (decimal?)null)
            ?? bid
            ?? ask
            ?? throw new PriceSourceException(sourceCode, "Response contained no usable price (no mid, bid or ask).");

        // Unknown symbols are not banded: each source owns its symbol check.
        if (PlausibleMid.TryGetValue(symbol, out var band) && (mid < band.Min || mid > band.Max))
        {
            throw new PriceSourceException(
                sourceCode,
                $"Mid {mid} for {symbol} is outside the plausible band {band.Min}–{band.Max} — likely an inverted or wrongly scaled rate.");
        }

        return new PriceQuote(symbol, observedAt.ToUniversalTime(), receivedAt.ToUniversalTime(), bid, ask, mid, sourceCode);
    }

    public PriceTick ToTick() => new()
    {
        Symbol = Symbol,
        ObservedAt = ObservedAt,
        ReceivedAt = ReceivedAt,
        Bid = Bid,
        Ask = Ask,
        Mid = Mid,
        SourceCode = SourceCode,
    };
}
