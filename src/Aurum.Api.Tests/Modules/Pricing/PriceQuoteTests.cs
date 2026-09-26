using Aurum.App.SharedKernel.Constants;
using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// What <c>PriceQuote.Normalize</c> owes the delta engine: a mid that is in the units the chart
/// assumes.
/// </summary>
/// <remarks>
/// The band exists for one defect that fails by succeeding. MetalpriceAPI returns both USD per
/// ounce and its inverse, ounces per USD; <c>PriceTick.Mid</c> is <c>numeric(18,4)</c>, so reading
/// the inverse persists <c>0.0002</c> with no exception anywhere and the delta engine reports a
/// near-total crash off a healthy feed.
/// </remarks>
public class PriceQuoteTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static PriceQuote Normalize(decimal? providerMid, string symbol = SupportedSymbol.Gold) =>
        PriceQuote.Normalize(symbol, At, At, bid: null, ask: null, providerMid, "metalprice-api");

    [Fact]
    public void A_real_gold_price_passes_the_band()
    {
        var quote = Normalize(4349.0684078017m);

        Assert.Equal(4349.0684078017m, quote.Mid);
    }

    [Fact]
    public void An_inverted_rate_is_rejected_rather_than_rounded_to_zero()
    {
        var ex = Assert.Throws<PriceSourceException>(() => Normalize(0.0002299343m));

        Assert.Equal("metalprice-api", ex.SourceCode);
        Assert.Contains("plausible band", ex.Message);
    }

    [Fact]
    public void A_derived_mid_is_banded_too()
    {
        // The band has to sit after the bid/ask fallback, not only on the providerMid path —
        // a provider that inverts its mid inverts its two-sided quote as well.
        var ex = Assert.Throws<PriceSourceException>(() => PriceQuote.Normalize(
            SupportedSymbol.Gold, At, At, bid: 0.00022m, ask: 0.00024m, providerMid: null, "metalprice-api"));

        Assert.Contains("plausible band", ex.Message);
    }

    [Fact]
    public void An_unbanded_symbol_is_passed_through()
    {
        // Platinum has no entry. Inventing a band for a metal we hold no reference price for would
        // fail the chain over on a source that is working.
        var quote = Normalize(1.23m, symbol: "XPTUSD");

        Assert.Equal(1.23m, quote.Mid);
    }
}
