namespace Aurum.App.Application.Prices.DTOs;

public sealed record PriceTickDto(
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    decimal Mid,
    decimal? Bid,
    decimal? Ask,
    string SourceCode);
