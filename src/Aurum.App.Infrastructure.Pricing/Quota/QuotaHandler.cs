using System.Net;
using Aurum.App.Logging;

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
    IAppLogService<QuotaHandler> logger) : DelegatingHandler
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

            await logger.LogAsync(LogType.SystemLog,
                "ProviderRejected",
                $"{sourceCode} rejected us for quota; local count was {status.Used}/{status.Limit}. Budget clamped until {status.ResetsAt:O}.",
                level: LogLevel.Warning,
                ct: ct
            );

            throw new Sources.QuotaExhaustedException(sourceCode, status.ResetsAt);
        }

        if (lease.Remaining <= 5)
        {
            await logger.LogAsync(LogType.SystemLog,
                "BudgetNearlyExhausted",
                $"{sourceCode} budget nearly spent: {lease.Remaining} requests left until {lease.ResetsAt:O}.",
                level: LogLevel.Warning,
                ct: ct
            );
        }

        return response;
    }
}
