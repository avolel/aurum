using System.Net;

namespace Aurum.App.Infrastructure.Pricing.Quota;

/// <summary>
/// Enforces a source's request budget at the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>A handler, not a wrapper around <see cref="Sources.IPriceSource"/>, so it counts every
/// request that leaves the process, retries included (D-7).</para>
/// <para>Takes <see cref="IServiceScopeFactory"/>: handlers are pooled for minutes, and a captured
/// governor would share one DbContext across concurrent requests.</para>
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
