using System.Text.Json.Serialization;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// GoldAPI.io free tier (§12's primary spot source).
/// </summary>
public class GoldApiIoSource(
    HttpClient http,
    TimeProvider clock,
    ILogger<GoldApiIoSource> logger) : IPriceSource
{
    /// <summary>Matched against <c>PriceSourceOptions.SourceCode</c>.</summary>
    public const string SourceCode = "goldapi.io";

    public string Code => SourceCode;

    public async Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        var (metal, currency) = SplitSymbol(symbol);

        using var response = await http.GetAsync($"api/{metal}/{currency}", ct);

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

        // Unix seconds. If missing, use receipt time: the epoch would be a decades-old tick.
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
