using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;

namespace Aurum.Api.Hubs;

/// <summary>
/// One symbol's held price and moves, as sent to apps.
/// </summary>
/// <param name="Quote">Null when the app holds no price yet: "connected, no price" rather than silence.</param>
/// <param name="Windows">
/// Keyed by <see cref="DeltaWindow.Code"/>, always all six. A null value is no answer, never zero (D-17).
/// </param>
public sealed record PriceUpdate(
    string Symbol,
    QuoteMessage? Quote,
    IReadOnlyDictionary<string, WindowMessage?> Windows)
{
    /// <summary>The one place pricing records become app messages; the hub and the sender both use it.</summary>
    public static PriceUpdate From(string symbol, LatestQuoteSnapshot? quote, DeltaSnapshot? deltas) =>
        new(
            symbol,
            quote is null ? null : QuoteMessage.From(quote),
            DeltaWindow.All.ToDictionary(
                window => window.Code,
                window => deltas?.Windows.GetValueOrDefault(window) is { } delta ? WindowMessage.From(delta) : null));
}

/// <remarks><c>AttemptedSources</c> stays on the server; item 8's sources endpoint serves it.</remarks>
public sealed record QuoteMessage(
    decimal Mid,
    decimal? Bid,
    decimal? Ask,
    DateTimeOffset ObservedAt,
    string SourceCode,
    bool IsFallback,
    TimeSpan Age,
    bool IsStale)
{
    public static QuoteMessage From(LatestQuoteSnapshot snapshot)
    {
        var quote = snapshot.Value.Quote;
        return new QuoteMessage(
            quote.Mid, quote.Bid, quote.Ask, quote.ObservedAt, quote.SourceCode,
            snapshot.Value.IsFallback, snapshot.Age, snapshot.IsStale);
    }
}

public sealed record WindowMessage(
    decimal DeltaPercent,
    decimal DeltaAbsolute,
    decimal VelocityPercentPerMinute,
    double? Volatility,
    int SampleCount,
    bool CrossSource,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public static WindowMessage From(WindowDelta delta) =>
        new(
            delta.DeltaPercent, delta.DeltaAbsolute, delta.VelocityPercentPerMinute, delta.Volatility,
            delta.SampleCount, delta.CrossSource, delta.Start.ObservedAt, delta.End.ObservedAt);
}
