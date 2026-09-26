using System.Security.Claims;
using Aurum.App.Application.AppLogs;

namespace Aurum.Api;

/// <summary>
/// Projects the current <see cref="HttpContext"/> onto <see cref="RequestContext"/>.
/// </summary>
/// <remarks>
/// The only place in the application that touches <c>HttpContext</c> outside a controller, and it
/// lives here rather than in the Application layer so that layer keeps its "no HTTP" rule. Outside
/// a request — the poller, the log drain — <c>HttpContext</c> is null and this returns
/// <see cref="RequestContext.None"/> rather than throwing, which is what lets background jobs use
/// the same <c>IAppLogService&lt;T&gt;</c> as controllers.
/// </remarks>
internal sealed class HttpRequestContextAccessor(IHttpContextAccessor accessor)
    : IRequestContextAccessor
{
    public RequestContext Current
    {
        get
        {
            var http = accessor.HttpContext;
            if (http is null)
            {
                return RequestContext.None;
            }

            return new RequestContext(
                UserId: http.User.FindFirstValue(ClaimTypes.NameIdentifier),
                TenantId: http.User.FindFirstValue("tenant_id"),

                // RemoteIpAddress is the socket peer, so behind a proxy it is the proxy. Reading
                // X-Forwarded-For here instead would trust a caller-supplied header; the correct
                // fix is UseForwardedHeaders with a known-proxy allowlist, which is a deployment
                // decision this class must not pre-empt.
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                RequestPath: http.Request.Path.Value,
                HttpMethod: http.Request.Method,

                // TraceIdentifier is per-connection-and-request and already appears in Serilog's
                // request log, so the two can be joined without inventing a second id.
                CorrelationId: http.TraceIdentifier);
        }
    }
}
