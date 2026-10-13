using Aurum.App.Application.Prices.Queries;
using Aurum.App.Application.Prices.Validators;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Validators;

public class GetLivePriceQueryValidatorTests
{
    private readonly GetLivePriceQueryValidator _validator = new();

    [Theory]
    [InlineData(SupportedSymbol.Gold)]
    [InlineData(SupportedSymbol.Silver)]
    [InlineData("xauusd")]
    public async Task ValidateAsync_WithSupportedSymbol_Passes(string symbol)
    {
        var result = await _validator.ValidateAsync(new GetLivePriceQuery(symbol));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XPTUSD")]
    [InlineData("XAUUSD ")]
    public async Task ValidateAsync_WithUnsupportedSymbol_FailsNamingTheSupportedOnes(string symbol)
    {
        var result = await _validator.ValidateAsync(new GetLivePriceQuery(symbol));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetLivePriceQuery.Symbol), error.PropertyName);
        Assert.Contains(SupportedSymbol.Gold, error.ErrorMessage);
    }
}
