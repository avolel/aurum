namespace Aurum.App.Infrastructure.Pricing.Sources;

public interface IPriceFeed
{
    /// <exception cref="AllSourcesFailedException">
    /// No source produced a quote. Carries a per-source account of why, including the sources
    /// never called because their circuit was open.
    /// </exception>
    Task<PriceFeedResult> GetLatestQuoteAsync(string symbol, CancellationToken ct);
}
