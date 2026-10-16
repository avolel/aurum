using Aurum.App.Application.Prices.Queries;
using Aurum.App.Application.Prices.Validators;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Validators;

public class GetPriceEventsQueryValidatorTests
{
    private readonly GetPriceEventsQueryValidator _validator = new();

    private static GetPriceEventsQuery Query(
        string symbol = SupportedSymbol.Gold, string? window = null, decimal? minPercent = null, string? cursor = null) =>
        new(symbol, window, minPercent, Limit: null, cursor);

    [Fact]
    public async Task ValidateAsync_WithNoFilters_Passes()
    {
        Assert.True((await _validator.ValidateAsync(Query())).IsValid);
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("5m")]
    [InlineData("15m")]
    [InlineData("1h")]
    [InlineData("4h")]
    [InlineData("1d")]
    public async Task ValidateAsync_WithEveryWindowCode_Passes(string window)
    {
        Assert.True((await _validator.ValidateAsync(Query(window: window))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5M")]
    [InlineData("30m")]
    public async Task ValidateAsync_WithUnknownWindow_FailsListingTheCodes(string window)
    {
        var error = Assert.Single((await _validator.ValidateAsync(Query(window: window))).Errors);

        Assert.Equal(nameof(GetPriceEventsQuery.Window), error.PropertyName);
        Assert.Contains("5m", error.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_WithZeroMinPercent_Passes()
    {
        Assert.True((await _validator.ValidateAsync(Query(minPercent: 0m))).IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithNegativeMinPercent_Fails()
    {
        var error = Assert.Single((await _validator.ValidateAsync(Query(minPercent: -0.0001m))).Errors);

        Assert.Equal(nameof(GetPriceEventsQuery.MinPercent), error.PropertyName);
    }

    [Fact]
    public async Task ValidateAsync_WithUnsupportedSymbol_Fails()
    {
        var error = Assert.Single((await _validator.ValidateAsync(Query(symbol: "XPTUSD"))).Errors);

        Assert.Equal(nameof(GetPriceEventsQuery.Symbol), error.PropertyName);
    }

    [Fact]
    public async Task ValidateAsync_WithIssuedCursor_Passes()
    {
        var cursor = new Cursor(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), 12).Encode();

        Assert.True((await _validator.ValidateAsync(Query(cursor: cursor))).IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithMalformedCursor_Fails()
    {
        var error = Assert.Single((await _validator.ValidateAsync(Query(cursor: "page-2"))).Errors);

        Assert.Equal(nameof(GetPriceEventsQuery.Cursor), error.PropertyName);
    }
}
