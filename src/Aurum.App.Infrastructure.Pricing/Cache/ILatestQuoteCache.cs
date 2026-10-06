using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <summary>
/// The newest known price per symbol, held in process so <c>/v1/price/live</c> can answer without
/// a database read (D-16).
/// </summary>
/// <remarks>
/// Public so item 8 can still choose to reference this project from Application.
/// </remarks>
public interface ILatestQuoteCache
{
    /// <summary>
    /// Offers a poll's result. Kept only if its <c>ObservedAt</c> is strictly newer than the
    /// quote already held for that symbol; an older or equal one is dropped without an error.
    /// </summary>
    void Record(PriceFeedResult result);

    /// <summary>
    /// The held quote with its age as of now, or null if there is no price for
    /// <paramref name="symbol"/>. Null so "no price" never looks like "a price of zero".
    /// </summary>
    LatestQuoteSnapshot? Get(string symbol);

    /// <summary>
    /// Loads the newest stored tick per supported symbol. Safe to call more than once.
    /// </summary>
    Task EnsureWarmAsync(CancellationToken ct);
}
