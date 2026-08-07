using System.Net;

namespace Aurum.Api.Modules.Pricing.Quota;

/// <summary>
/// Enforces a source's request budget at the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// Placement is the point. Phase 1 adds Polly retries and a failover chain above
/// <see cref="Sources.IPriceSource"/>; a governor sitting at that level would let every retry
/// and every failover attempt spend quota without being counted. As a DelegatingHandler this
/// sees exactly the requests that actually leave the process — including retries — which is
/// what the provider bills.
/// </para>
/// <para>
/// It takes <see cref="IServiceScopeFactory"/> rather than <see cref="IQuotaGovernor"/> because
/// IHttpClientFactory pools message handlers for minutes at a time. Injecting the governor
/// directly would capture its scoped DbContext in a long-lived object — a DbContext shared
/// across concurrent requests, which is not thread-safe.
/// </para>
/// </remarks>
public class QuotaHandler(
    IServiceScopeFactory scopeFactory,
    string sourceCode,
    ILogger<QuotaHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        QuotaLease lease;
        using (var scope = scopeFactory.CreateScope())
        {
            var governor = scope.ServiceProvider.GetRequiredService<IQuotaGovernor>();
            lease = await governor.AcquireAsync(sourceCode, ct);
        }

        if (!lease.Granted)
        {
            throw new Sources.QuotaExhaustedException(sourceCode, lease.ResetsAt);
        }

        var response = await base.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
        {
            response.Dispose();

            using var scope = scopeFactory.CreateScope();
            var governor = scope.ServiceProvider.GetRequiredService<IQuotaGovernor>();
            await governor.ReportProviderRejectionAsync(sourceCode, ct);
            var status = await governor.GetStatusAsync(sourceCode, ct);

            logger.LogWarning(
                "{Source} rejected us for quota; local count was {Used}/{Limit}. Budget clamped until {ResetsAt:O}.",
                sourceCode, status.Used, status.Limit, status.ResetsAt);

            throw new Sources.QuotaExhaustedException(sourceCode, status.ResetsAt);
        }

        if (lease.Remaining <= 5)
        {
            logger.LogWarning("{Source} budget nearly spent: {Remaining} requests left until {ResetsAt:O}.",
                sourceCode, lease.Remaining, lease.ResetsAt);
        }

        return response;
    }
}
