using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Sources;

public class MetalPriceApiSource(
    HttpClient http,
    TimeProvider clock,
    ILogger<MetalPriceApiSource> logger) : IPriceSource
{
    public const string SourceCode = "metalprice-api";

    public string Code => SourceCode;

    public async Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        // Checked before the request so no lease is spent. PriceSourceException, not
        // ArgumentException, so the chain can fail over to a source that carries this metal.
        if (!string.Equals(symbol, SupportedSymbol.Gold, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSourceException(Code, $"Supports {SupportedSymbol.Gold} only; asked for '{symbol}'.");
        }

        using var response = await http.GetAsync("v1/latest?base=XAU&currencies=USD", ct);

        if (!response.IsSuccessStatusCode)
            throw new PriceSourceException(Code, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        var receivedAt = clock.GetUtcNow();

        MetalApiGoldResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<MetalApiGoldResponse>(ct);
        }
        catch (Exception ex)
        {
            throw new PriceSourceException(Code, "Response body was not the expected JSON.", ex);
        }

        // Read `USD` (USD per ounce). The `XAUUSD` key is the inverse, ounces per USD, and would
        // round to 0.0002 without an error; PriceQuote.Normalize's band backs this up.
        if (body is not { Success: true, Rates: not null }
            || !body.Rates.TryGetValue("USD", out var mid)
            || mid <= 0)
            throw new PriceSourceException(Code, "Response contained no positive USD rate for XAU.");

        // Unix seconds. If missing, use receipt time: the epoch would be a decades-old tick.
        DateTimeOffset observedAt;
        if (body.Timestamp is > 0)
            observedAt = DateTimeOffset.FromUnixTimeSeconds(body.Timestamp.Value);
        else
        {
            observedAt = receivedAt;
            logger.LogWarning("{Source} returned no usable timestamp; using receipt time for {Symbol}.", Code, symbol);
        }

        // This provider does not quote bid and ask, so its price is the mid.
        return PriceQuote.Normalize(
            symbol,
            observedAt,
            receivedAt,
            bid: null,
            ask: null,
            providerMid: mid,
            Code);
    }
}
