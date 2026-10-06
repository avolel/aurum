using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// How much each symbol's price moved over each <see cref="DeltaWindow"/> (FR-1.4), from an
/// in-memory history of recent prices.
/// </summary>
/// <remarks>
/// A window's move comes from two real prices spaced close enough to its length, or it is null.
/// Null means "unknown" and must never be shown as zero (D-17). Public for the same item 8 reason
/// as <c>ILatestQuoteCache</c>.
/// </remarks>
public interface IDeltaEngine
{
    /// <summary>
    /// Adds a polled price. A price at or before the newest held one for its symbol is dropped and
    /// counted in <see cref="DeltaSnapshot.DroppedOutOfOrder"/>; the history is never re-sorted.
    /// </summary>
    void Record(PriceQuote quote);

    /// <summary>
    /// Every window's move for <paramref name="symbol"/> as of now, or null if this process has
    /// never held a price for it.
    /// </summary>
    DeltaSnapshot? GetSnapshot(string symbol);

    /// <summary>
    /// Loads recent stored prices per supported symbol. Loads at most once: later callers wait for
    /// it and then skip, and a failed load lets the next caller try again.
    /// </summary>
    Task EnsureWarmAsync(CancellationToken ct);
}
