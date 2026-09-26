namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// One upstream quote provider. Implementations normalize to <see cref="PriceQuote"/> and do
/// no persistence, no caching and no retrying — Phase 1 layers the failover chain and circuit
/// breaker on top of this interface, and quota accounting happens below it at the HTTP handler.
/// </summary>
public interface IPriceSource
{
    /// <summary>Stable code matching <see cref="Entities.PriceSource.Code"/> and the options key.</summary>
    string Code { get; }

    /// <summary>Failover order (FR-1.3); lower is preferred.</summary>
    int Priority { get; }

    /// <summary>
    /// Fetch the latest quote for <paramref name="symbol"/>.
    /// </summary>
    /// <exception cref="QuotaExhaustedException">
    /// The source's request budget for the current period is spent. Callers should move to the
    /// next source rather than retrying — retrying cannot succeed until the period rolls.
    /// </exception>
    /// <exception cref="PriceSourceException">
    /// The provider was reachable but unusable (bad status, unparseable body, missing fields).
    /// </exception>
    Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct);
}
