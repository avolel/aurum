using System.Net;
using System.Text.Json.Serialization;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// GoldAPI.io free tier (§12's primary spot source).
/// </summary>
/// <remarks>
/// This class does not touch quota accounting. That lives in the <see cref="Quota.QuotaHandler"/>
/// on this client's HTTP pipeline, so that Phase 1's retries and failover cannot spend budget
/// behind the governor's back.
/// </remarks>
public class GoldApiIoSource(
    HttpClient http,
    IOptions<PriceSourcesOptions> options,
    TimeProvider clock,
    ILogger<GoldApiIoSource> logger) : IPriceSource
{
    /// <summary>
    /// The provider's natural key, matched against <c>PriceSourceOptions.SourceCode</c>. It lives
    /// on the source rather than on an options class because it identifies this implementation,
    /// not a configuration shape — options are now looked up by it, not selected by it.
    /// </summary>
    public const string SourceCode = "goldapi.io";

    private readonly PriceSourceOptions _options = options.Value.RequireByCode(SourceCode);

    public string Code => SourceCode;

    public int Priority => _options.Priority;

    public async Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        var (metal, currency) = SplitSymbol(symbol);

        using var response = await http.GetAsync($"api/{metal}/{currency}", ct);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
        {
            // The handler translates these into QuotaExhaustedException after recording the
            // provider's correction; reaching here means the pipeline was misconfigured.
            throw new PriceSourceException(Code, $"Quota status {(int)response.StatusCode} reached the source unhandled — check the HTTP pipeline wiring.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PriceSourceException(Code, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var receivedAt = clock.GetUtcNow();

        GoldApiQuoteResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<GoldApiQuoteResponse>(ct);
        }
        catch (Exception ex)
        {
            throw new PriceSourceException(Code, "Response body was not the expected JSON.", ex);
        }

        if (body is null || body.Price is null or <= 0)
        {
            throw new PriceSourceException(Code, "Response contained no positive price.");
        }

        // GoldAPI reports `timestamp` as Unix seconds. A missing or zero value means we cannot
        // trust the provider's own validity time, so fall back to receipt time and say so —
        // silently backdating to the epoch would poison the delta engine.
        DateTimeOffset observedAt;
        if (body.Timestamp is > 0)
        {
            observedAt = DateTimeOffset.FromUnixTimeSeconds(body.Timestamp.Value);
        }
        else
        {
            observedAt = receivedAt;
            logger.LogWarning("{Source} returned no usable timestamp; using receipt time for {Symbol}.", Code, symbol);
        }

        return PriceQuote.Normalize(
            symbol,
            observedAt,
            receivedAt,
            body.Bid,
            body.Ask,
            body.Price,
            Code);
    }

    /// <summary>
    /// GoldAPI addresses instruments as /api/{metal}/{currency}, so XAUUSD becomes XAU/USD.
    /// </summary>
    private static (string Metal, string Currency) SplitSymbol(string symbol)
    {
        if (symbol.Length != 6)
        {
            throw new ArgumentException($"Expected a 6-character symbol like XAUUSD, got '{symbol}'.", nameof(symbol));
        }

        return (symbol[..3].ToUpperInvariant(), symbol[3..].ToUpperInvariant());
    }

    private sealed record GoldApiQuoteResponse
    {
        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; init; }

        /// <summary>GoldAPI's headline price for the metal, in the requested currency per troy ounce.</summary>
        [JsonPropertyName("price")]
        public decimal? Price { get; init; }

        [JsonPropertyName("bid")]
        public decimal? Bid { get; init; }

        [JsonPropertyName("ask")]
        public decimal? Ask { get; init; }
    }
}
