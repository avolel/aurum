using System.Text.Json.Serialization;

namespace Aurum.App.Infrastructure.Pricing.Sources;

public sealed record MetalApiGoldResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("base")]
    public string? Base { get; init; }

    // Unix seconds.
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    // A map because the keys follow the `currencies` parameter. Decimal so tiny rates keep precision.
    [JsonPropertyName("rates")]
    public Dictionary<string, decimal>? Rates { get; init; }
}
