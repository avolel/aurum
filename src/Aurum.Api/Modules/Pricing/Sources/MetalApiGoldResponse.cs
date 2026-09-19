using System.Text.Json.Serialization;

namespace Aurum.Api.Modules.Pricing.Sources;

public sealed record MetalApiGoldResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("base")]
    public string? Base { get; init; }

    // Unix seconds.
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    // A map rather than a property per currency: the keys depend on the `currencies` query
    // parameter, so a fixed shape would silently bind nulls when the request changes. Decimal
    // rather than double so a rate like 0.0002299343 is not rounded before we decide how to use it.
    [JsonPropertyName("rates")]
    public Dictionary<string, decimal>? Rates { get; init; }
}
