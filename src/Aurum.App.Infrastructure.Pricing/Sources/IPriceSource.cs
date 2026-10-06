namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// One upstream quote provider. Returns a <see cref="PriceQuote"/>; no saving, caching or retrying.
/// Failover sits above it, quota accounting below it in the HTTP pipeline.
/// </summary>
public interface IPriceSource
{
    /// <summary>Stable code matching <see cref="Entities.PriceSource.Code"/> and the options key.</summary>
    string Code { get; }

    /// <summary>
    /// Fetch the latest quote for <paramref name="symbol"/>.
    /// </summary>
    /// <exception cref="QuotaExhaustedException">
    /// The budget for this period is spent. Move to the next source; a retry cannot succeed.
    /// </exception>
    /// <exception cref="PriceSourceException">
    /// The provider was reachable but unusable (bad status, unparseable body, missing fields).
    /// </exception>
    Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct);
}
