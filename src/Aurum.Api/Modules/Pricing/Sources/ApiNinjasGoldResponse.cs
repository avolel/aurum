using System.Text.Json.Serialization;

namespace Aurum.Api.Modules.Pricing.Sources;

public sealed record ApiNinjasGoldResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("updated")]
    public long? Updated { get; init; }
}
