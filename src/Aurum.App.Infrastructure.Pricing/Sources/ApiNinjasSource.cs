using System.Net;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// API Ninjas' gold endpoint (§12's secondary source).
/// </summary>
/// <remarks>
/// <para>Unlike GoldAPI this provider is <b>gold-only</b>: /v1/goldprice takes no instrument
/// parameter, so the <c>symbol</c> argument has nowhere to go. Without the guard below, a request
/// for XAGUSD returns gold's price labelled as silver — a well-formed row in price_ticks that
/// nothing downstream can distinguish from a real one.</para>
/// <para>The free tier serves a 15-minute-delayed futures price and forbids commercial use
/// (BRD §12), so this source must be paid for or replaced before Phase 5 billing.</para>
/// <para>Quota accounting lives in <see cref="Quota.QuotaHandler"/> on this client's pipeline,
/// not here.</para>
/// </remarks>
public class ApiNinjasSource(
    HttpClient http,
    IOptions<PriceSourcesOptions> options,
    TimeProvider clock,
    ILogger<ApiNinjasSource> logger) : IPriceSource
{
    public const string SourceCode = "api-ninjas";
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

        using var response = await http.GetAsync("v1/goldprice", ct);

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

        // `updated` is Unix seconds. Missing or zero means the provider's own validity time is
        // unusable; fall back to receipt time and say so, because backdating to the epoch would
        // hand the delta engine a decades-old tick.
        DateTimeOffset observedAt;
        if (body.Updated is > 0)
            observedAt = DateTimeOffset.FromUnixTimeSeconds(body.Updated.Value);
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
            providerMid: body.Price,
            Code);
    }
}
