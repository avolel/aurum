using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// API Ninjas' gold endpoint (§12's secondary source).
/// </summary>
/// <remarks>
/// <para><b>Gold only</b>: /v1/goldprice takes no symbol, so without the guard below XAGUSD would
/// get gold's price labelled as silver.</para>
/// <para>The free tier is a 15-minute-delayed futures price and forbids commercial use (BRD §12):
/// pay for or replace it before Phase 5 billing.</para>
/// </remarks>
public class ApiNinjasSource(
    HttpClient http,
    TimeProvider clock,
    ILogger<ApiNinjasSource> logger) : IPriceSource
{
    public const string SourceCode = "api-ninjas";

    public string Code => SourceCode;

    public async Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        // Checked before the request so no lease is spent. PriceSourceException, not
        // ArgumentException, so the chain can fail over to a source that carries this metal.
        if (!string.Equals(symbol, SupportedSymbol.Gold, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSourceException(Code, $"Supports {SupportedSymbol.Gold} only; asked for '{symbol}'.");
        }

        using var response = await http.GetAsync("v1/goldprice", ct);

        if (!response.IsSuccessStatusCode)
            throw new PriceSourceException(Code, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        var receivedAt = clock.GetUtcNow();

        ApiNinjasGoldResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ApiNinjasGoldResponse>(ct);
        }
        catch (Exception ex)
        {
            throw new PriceSourceException(Code, "Response body was not the expected JSON.", ex);
        }

        if (body is null || body.Price is null or <= 0)
        {
            throw new PriceSourceException(Code, "Response contained no positive price.");
        }

        // Unix seconds. If missing, use receipt time: the epoch would be a decades-old tick.
        DateTimeOffset observedAt;
        if (body.Updated is > 0)
            observedAt = DateTimeOffset.FromUnixTimeSeconds(body.Updated.Value);
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
            providerMid: body.Price,
            Code);
    }
}
