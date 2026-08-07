using Aurum.Api.Modules.Pricing.Entities;

namespace Aurum.Api.Modules.Pricing.Sources;

/// <summary>
/// The canonical tick shape from FR-1.2, before persistence.
/// </summary>
/// <param name="Symbol">e.g. <c>XAUUSD</c>.</param>
/// <param name="ObservedAt">Provider-reported validity time, in UTC.</param>
/// <param name="ReceivedAt">When we read the response. Drives the staleness we show the user.</param>
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
    /// Builds a quote, deriving the mid when the provider does not give one directly.
    /// Prefers the provider's own mid: for some sources it is the traded last price rather
    /// than the midpoint, and substituting our own would silently change what the chart means.
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
