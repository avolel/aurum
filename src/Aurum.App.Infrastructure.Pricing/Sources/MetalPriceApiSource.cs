using System.Net;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Sources;

public class MetalPriceApiSource(
    HttpClient http,
    IOptions<PriceSourcesOptions> options,
    TimeProvider clock,
    ILogger<MetalPriceApiSource> logger) : IPriceSource
{
    public const string SourceCode = "metalprice-api";
    private readonly PriceSourceOptions _options = options.Value.RequireByCode(SourceCode);

    public string Code => SourceCode;

    public int Priority => _options.Priority;

    public async Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        // Before the request, not after: QuotaHandler charges a lease the moment anything leaves
        // the process and never refunds it, so validating on the response would spend budget to
        // learn something knowable up front.
        //
        // PriceSourceException rather than ArgumentException because the failover chain must be
        // able to move to a source that does carry this metal. ArgumentException is reserved for
        // a symbol that is structurally invalid, which no source can serve.
        if (!string.Equals(symbol, SupportedSymbol.Gold, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSourceException(Code, $"Supports {SupportedSymbol.Gold} only; asked for '{symbol}'.");
        }

        using var response = await http.GetAsync("v1/latest?base=XAU&currencies=USD", ct);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
        {
            // QuotaHandler translates these into QuotaExhaustedException and disposes the response,
            // so this is unreachable with a correctly wired pipeline. It is an assertion that the
            // handler is present, not a failover point.
            throw new PriceSourceException(Code, $"Quota status {(int)response.StatusCode} reached the source unhandled — check the HTTP pipeline wiring.");
        }

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

        // With `base=XAU` the requested currency is keyed by itself and carries USD per ounce
        // (~4,349). The provider also ships a convenience key of base+currency — `XAUUSD` here —
        // holding the inverse, ounces per USD (~0.00023). Reading that one rounds to 0.0002 in
        // `numeric(18,4)` and never throws, which is why the band in PriceQuote.Normalize backs
        // this up rather than trusting the key name alone.
        if (body is not { Success: true, Rates: not null }
            || !body.Rates.TryGetValue("USD", out var mid)
            || mid <= 0)
            throw new PriceSourceException(Code, "Response contained no positive USD rate for XAU.");

        // `timestamp` is Unix seconds. Missing or zero means the provider's own validity time is
        // unusable; fall back to receipt time and say so, because backdating to the epoch would
        // hand the delta engine a decades-old tick.
        DateTimeOffset observedAt;
        if (body.Timestamp is not null && body.Timestamp.Value > 0)
            observedAt = DateTimeOffset.FromUnixTimeSeconds(body.Timestamp.Value);
        else
        {
            observedAt = receivedAt;
            logger.LogWarning("{Source} returned no usable timestamp; using receipt time for {Symbol}.", Code, symbol);
        }

        // Bid and ask stay null: this provider genuinely does not quote two-sided. Normalize takes
        // the provider's price as the mid directly.
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
