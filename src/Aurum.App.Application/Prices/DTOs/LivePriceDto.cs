namespace Aurum.App.Application.Prices.DTOs;

/// <param name="AttemptedSources">Empty when the price was loaded from the database at startup.</param>
/// <param name="AgeSeconds">Seconds since <paramref name="ObservedAt"/>, as of this request.</param>
public sealed record LivePriceDto(
    string Symbol,
    decimal Mid,
    decimal? Bid,
    decimal? Ask,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string SourceCode,
    bool IsFallback,
    IReadOnlyList<string> AttemptedSources,
    double AgeSeconds,
    bool IsStale);
