using Aurum.App.Application.Prices.Queries;
using Aurum.App.Application.Prices.Validators;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Validators;

public class GetPriceHistoryQueryValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    // Typed in, not computed from PriceTick.Retention, so a changed constant shows up here too.
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly GetPriceHistoryQueryValidator _validator = new(new FakeTimeProvider(Now));

    private static GetPriceHistoryQuery Query(
        DateTimeOffset? from, DateTimeOffset? to = null, string symbol = SupportedSymbol.Gold, string? cursor = null) =>
        new(symbol, from, to, Limit: null, cursor);

    [Fact]
    public async Task ValidateAsync_WithRecentFromAndNoTo_Passes()
    {
        var result = await _validator.ValidateAsync(Query(Now.AddDays(-1)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithFromExactlyAtRetentionCutoff_Passes()
    {
        var result = await _validator.ValidateAsync(Query(Cutoff));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithFromOneTickBeforeRetentionCutoff_FailsNamingThirtyDays()
    {
        var result = await _validator.ValidateAsync(Query(Cutoff.AddTicks(-1)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.From), error.PropertyName);
        Assert.Contains("30 days", error.ErrorMessage);
        Assert.Contains("2026-09-13T12:00:00", error.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_WithNoFrom_FailsAsRequired()
    {
        var result = await _validator.ValidateAsync(Query(from: null));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.From), error.PropertyName);
    }

    [Theory]
    [InlineData(0)]   // to == from: an empty half-open range
    [InlineData(-1)]  // to before from
    public async Task ValidateAsync_WithToNotAfterFrom_Fails(int toOffsetHours)
    {
        var from = Now.AddDays(-1);

        var result = await _validator.ValidateAsync(Query(from, to: from.AddHours(toOffsetHours)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.To), error.PropertyName);
    }

    [Fact]
    public async Task ValidateAsync_WithFromInTheFutureAndNoTo_Fails()
    {
        var result = await _validator.ValidateAsync(Query(Now.AddMinutes(1)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.To), error.PropertyName);
    }

    [Fact]
    public async Task ValidateAsync_WithUnsupportedSymbol_Fails()
    {
        var result = await _validator.ValidateAsync(Query(Now.AddDays(-1), symbol: "XPTUSD"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.Symbol), error.PropertyName);
    }

    [Fact]
    public async Task ValidateAsync_WithIssuedCursor_Passes()
    {
        var cursor = new Cursor(Now.AddHours(-2), 17).Encode();

        var result = await _validator.ValidateAsync(Query(Now.AddDays(-1), cursor: cursor));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithMalformedCursor_Fails()
    {
        var result = await _validator.ValidateAsync(Query(Now.AddDays(-1), cursor: "page-2"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetPriceHistoryQuery.Cursor), error.PropertyName);
    }
}
