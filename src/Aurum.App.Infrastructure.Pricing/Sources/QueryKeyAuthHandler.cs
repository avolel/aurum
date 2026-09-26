using Microsoft.AspNetCore.WebUtilities;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// Appends MetalpriceAPI's api_key query parameter.
/// </summary>
/// <remarks>
/// The key lives here rather than in the source so that no exception or log line the source
/// produces is capable of carrying it — a URI in a PriceSourceException message is the leak
/// this placement makes structurally impossible rather than merely forbidden.
/// </remarks>
internal sealed class QueryKeyAuthHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        // Idempotent by design: Polly retries reuse this HttpRequestMessage, so the handler runs
        // again on a URI it already rewrote. Appending unconditionally duplicates the parameter.
        if (!QueryHelpers.ParseQuery(uri.Query).ContainsKey("api_key"))
        {
            request.RequestUri = new Uri(QueryHelpers.AddQueryString(uri.ToString(), "api_key", apiKey));
        }
        return base.SendAsync(request, ct);
    }
}
