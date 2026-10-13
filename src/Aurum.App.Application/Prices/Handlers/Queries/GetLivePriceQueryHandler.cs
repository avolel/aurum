using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Pricing.Cache;
using MediatR;

namespace Aurum.App.Application.Prices.Handlers.Queries;

/// <remarks>
/// Reads the in-memory cache only. Never calls a price service: every call spends part of a
/// 100-request month, and this endpoint can be reloaded freely.
/// </remarks>
public sealed class GetLivePriceQueryHandler(ILatestQuoteCache cache)
    : IRequestHandler<GetLivePriceQuery, LivePriceDto?>
{
    public Task<LivePriceDto?> Handle(GetLivePriceQuery request, CancellationToken cancellationToken)
    {
        // The cache ignores case, so "xauusd" finds the held quote; the reply uses its stored spelling.
        if (cache.Get(request.Symbol) is not { } snapshot)
        {
            return Task.FromResult<LivePriceDto?>(null);
        }

        var quote = snapshot.Value.Quote;
        return Task.FromResult<LivePriceDto?>(new LivePriceDto(
            quote.Symbol,
            quote.Mid,
            quote.Bid,
            quote.Ask,
            quote.ObservedAt,
            quote.ReceivedAt,
            quote.SourceCode,
            snapshot.Value.IsFallback,
            snapshot.Value.AttemptedSources,
            snapshot.Age.TotalSeconds,
            snapshot.IsStale));
    }
}
