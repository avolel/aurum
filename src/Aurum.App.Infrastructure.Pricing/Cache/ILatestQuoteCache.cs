using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <summary>
/// The newest known price per symbol, held in process so <c>/v1/price/live</c> can answer without
/// a database read (D-16).
/// </summary>
/// <remarks>
/// Public, with both records, although only <c>Program.cs</c> names the implementation. Item 8
/// has to decide whether Application references this project or redeclares the interface, and
/// making these internal would take the first option away before that decision is made.
/// </remarks>
public interface ILatestQuoteCache
{
    /// <summary>
    /// Offers a poll's result. Kept only if its <c>ObservedAt</c> is strictly newer than the
    /// quote already held for that symbol; an older or equal one is dropped without an error.
    /// </summary>
    void Record(PriceFeedResult result);

    /// <summary>
    /// The held quote with its age as of now, or null if this process has no price for
    /// <paramref name="symbol"/>. Null, not an empty snapshot: "no price" and "a price of zero"
    /// must not look alike.
    /// </summary>
    LatestQuoteSnapshot? Get(string symbol);

    /// <summary>
    /// Loads the newest stored tick per supported symbol. Safe to call more than once, and
    /// awaited by the poller before its first poll so a restart does not serve nothing for a
    /// whole interval.
    /// </summary>
    Task EnsureWarmAsync(CancellationToken ct);
}
