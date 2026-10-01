using Aurum.App.Infrastructure.Pricing.Sources;

namespace Aurum.App.Infrastructure.Pricing.Cache;

/// <summary>
/// What <see cref="ILatestQuoteCache"/> holds per symbol. Immutable, so a reader always sees one
/// whole record, never a half-replaced one.
/// </summary>
/// <remarks>
/// Deliberately does not carry <see cref="SourceAttempt"/>. That record holds an
/// <see cref="Exception"/> for logging, so keeping it here would pin a stack trace — and whatever
/// the transport hung off it — for as long as the quote stays current, which can be hours.
/// <c>LatestQuoteCacheTests.A_cached_quote_holds_no_exception_reference</c> pins that.
/// </remarks>
/// <param name="AttemptedSources">
/// Sources the poll actually called, in order. Empty means "not observed by this process" — a
/// quote loaded from the database at startup — not "no source was called". Item 8 must not render
/// an empty list as a failed chain.
/// </param>
public sealed record LatestQuote(
    PriceQuote Quote,
    bool IsFallback,
    IReadOnlyList<string> AttemptedSources);

/// <summary>
/// A <see cref="LatestQuote"/> with its freshness computed at the moment it was read.
/// </summary>
/// <remarks>
/// Age is never stored. Computing it on read means a caller cannot forget to pass the clock, and
/// cannot read a stale flag that was computed while the quote was still fresh.
/// </remarks>
/// <param name="Age">
/// <c>now - Quote.ObservedAt</c>: the whole age of the price, provider lag included.
/// </param>
public sealed record LatestQuoteSnapshot(
    LatestQuote Value,
    TimeSpan Age,
    bool IsStale);
