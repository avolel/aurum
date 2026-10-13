using Aurum.App.Application.Prices.Handlers.Queries;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Moq;

namespace Aurum.Api.Tests.Handlers;

/// <remarks>No container: this handler reads the in-memory cache only.</remarks>
public class GetLivePriceQueryHandlerTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly Mock<ILatestQuoteCache> _cache = new();

    [Fact]
    public async Task Handle_WithHeldPrice_ReturnsEveryField()
    {
        var quote = new PriceQuote(
            SupportedSymbol.Gold, ObservedAt, ObservedAt.AddSeconds(2), 2650.10m, 2650.50m, 2650.30m, "api-ninjas.com");
        _cache.Setup(c => c.Get("xauusd")).Returns(new LatestQuoteSnapshot(
            new LatestQuote(quote, IsFallback: true, AttemptedSources: ["goldapi.io", "api-ninjas.com"]),
            Age: TimeSpan.FromSeconds(90),
            IsStale: false));

        var dto = await new GetLivePriceQueryHandler(_cache.Object)
            .Handle(new GetLivePriceQuery("xauusd"), CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(SupportedSymbol.Gold, dto.Symbol); // stored spelling, not the caller's
        Assert.Equal(2650.30m, dto.Mid);
        Assert.Equal(2650.10m, dto.Bid);
        Assert.Equal(2650.50m, dto.Ask);
        Assert.Equal(ObservedAt, dto.ObservedAt);
        Assert.Equal(ObservedAt.AddSeconds(2), dto.ReceivedAt);
        Assert.Equal("api-ninjas.com", dto.SourceCode);
        Assert.True(dto.IsFallback);
        Assert.Equal(["goldapi.io", "api-ninjas.com"], dto.AttemptedSources);
        Assert.Equal(90, dto.AgeSeconds);
        Assert.False(dto.IsStale);
    }

    [Fact]
    public async Task Handle_WithNoHeldPrice_ReturnsNull()
    {
        _cache.Setup(c => c.Get(It.IsAny<string>())).Returns((LatestQuoteSnapshot?)null);

        var dto = await new GetLivePriceQueryHandler(_cache.Object)
            .Handle(new GetLivePriceQuery(SupportedSymbol.Gold), CancellationToken.None);

        Assert.Null(dto);
    }
}
