using System.Security.Claims;
using Aurum.App.Application.AppLogs;

namespace Aurum.Api;

/// <summary>
/// Projects the current <see cref="HttpContext"/> onto <see cref="RequestContext"/>. Lives here so the
/// Application layer stays free of HTTP. Outside a request it returns <see cref="RequestContext.None"/>,
/// so background jobs can share <c>IAppLogService&lt;T&gt;</c>.
/// </summary>
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

                // The socket peer, so a proxy behind one. Fix with UseForwardedHeaders and a
                // known-proxy list at deployment, never by trusting X-Forwarded-For here.
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                RequestPath: http.Request.Path.Value,
                HttpMethod: http.Request.Method,

                // Already in Serilog's request log, so the two join without a second id.
                CorrelationId: http.TraceIdentifier);
        }
    }
}
