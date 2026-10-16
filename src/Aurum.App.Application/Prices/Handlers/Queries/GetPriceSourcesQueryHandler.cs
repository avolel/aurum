using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Sources;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Application.Prices.Handlers.Queries;

/// <summary>
/// Joins four views of each configured source by source code: settings, the <c>price_sources</c>
/// row, the in-memory circuit, and the quota ledger.
/// </summary>
/// <remarks>
/// Settings are the list: a row or circuit with no settings entry is not a source the app uses.
/// Never calls a price service.
/// </remarks>
public sealed class GetPriceSourcesQueryHandler(
    IOptions<PriceSourcesOptions> options,
    AurumDbContext db,
    SourceCircuitStore circuits,
    IQuotaGovernor quota)
    : IRequestHandler<GetPriceSourcesQuery, IReadOnlyList<PriceSourceStatusDto>>
{
    public async Task<IReadOnlyList<PriceSourceStatusDto>> Handle(
        GetPriceSourcesQuery request, CancellationToken cancellationToken)
    {
        // Ignore case, as RequireByCode does when it matches settings to a code.
        var rows = await db.PriceSources.AsNoTracking()
            .ToDictionaryAsync(r => r.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var circuitsByCode = circuits.SnapshotAll()
            .ToDictionary(c => c.SourceCode, StringComparer.OrdinalIgnoreCase);

        var result = new List<PriceSourceStatusDto>();

        // Same order as EnabledInFailoverOrder, with switched-off sources mixed in by priority.
        foreach (var source in options.Value.Sources.Values
                     .OrderBy(s => s.Priority)
                     .ThenBy(s => s.SourceCode, StringComparer.Ordinal))
        {
            var row = rows.GetValueOrDefault(source.SourceCode);
            var circuit = circuitsByCode.GetValueOrDefault(source.SourceCode);

            // One at a time: the governor shares this handler's DbContext, which is not thread-safe.
            var status = source.Enabled ? await quota.GetStatusAsync(source.SourceCode, cancellationToken) : (QuotaStatus?)null;

            result.Add(new PriceSourceStatusDto(
                source.SourceCode,
                row?.DisplayName,
                source.Priority,
                source.Enabled,
                source.MonthlyRequestLimit,
                row?.LastSuccessAt,
                row?.LastFailureAt,
                row?.LastFailureReason,
                circuit is null ? null : new CircuitStatusDto(circuit.IsOpen, circuit.ConsecutiveFailures, circuit.OpenedUntil),
                status is not { } s ? null : new QuotaStatusDto(
                    s.Used,
                    s.Limit,
                    // A provider rejection clamps the period to zero; the counter never carries it (D-7).
                    Remaining: s.ProviderRejected ? 0 : s.Limit - s.Used,
                    s.ResetsAt,
                    s.ProviderRejected)));
        }

        return result;
    }
}
