using Microsoft.AspNetCore.WebUtilities;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// Appends MetalpriceAPI's api_key query parameter.
/// </summary>
/// <remarks>
/// Added here, not in the source, so nothing the source logs or throws can contain the key.
/// </remarks>
internal sealed class QueryKeyAuthHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        // Retries reuse this request, so only add the key once.
        if (!QueryHelpers.ParseQuery(uri.Query).ContainsKey("api_key"))
        {
            request.RequestUri = new Uri(QueryHelpers.AddQueryString(uri.ToString(), "api_key", apiKey));
        }
        return base.SendAsync(request, ct);
    }
}
