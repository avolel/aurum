using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <summary>
/// What <see cref="ILatestQuoteCache"/> holds per symbol. Immutable, so a reader always sees one
/// whole record, never a half-replaced one.
/// </summary>
/// <remarks>
/// Must not carry <see cref="SourceAttempt"/>: its Exception would be held for hours (D-16).
/// </remarks>
/// <param name="AttemptedSources">
/// Sources the poll called, in order. Empty means "loaded at startup", not "nothing was called".
/// </param>
public sealed record LatestQuote(
    PriceQuote Quote,
    bool IsFallback,
    IReadOnlyList<string> AttemptedSources);

/// <summary>
/// A <see cref="LatestQuote"/> with its freshness computed at the moment it was read.
/// </summary>
/// <param name="Age">
/// <c>now - Quote.ObservedAt</c>: the whole age of the price, provider lag included.
/// </param>
public sealed record LatestQuoteSnapshot(
    LatestQuote Value,
    TimeSpan Age,
    bool IsStale);
