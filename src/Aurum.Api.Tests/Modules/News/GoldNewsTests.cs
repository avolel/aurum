using Aurum.App.Infrastructure.Pricing.News;

namespace Aurum.Api.Tests.Modules.News;

/// <summary>The keep-or-skip rule for feed items. Pure; no container.</summary>
public class GoldNewsTests
{
    [Theory]
    [InlineData("Gold hits record")]
    [InlineData("Fed holds rates")]
    [InlineData("GOLD SLIPS AS YIELDS RISE")]                  // case ignored
    [InlineData("Gold's three black crows pattern")]          // possessive
    [InlineData("XAU/USD: buyers return above $3,500")]
    [InlineData("Federal Reserve minutes show split")]
    [InlineData("Treasury yields jump after jobs report")]
    [InlineData("Investors pile into safe-haven assets")]
    [InlineData("Precious metals extend losses")]
    [InlineData("US CPI cools to 2.4%")]
    public void Title_WithKeyword_IsRelated(string title) =>
        Assert.True(GoldNews.IsRelated(title, extract: null));

    [Theory]
    [InlineData("Goldman Sachs earnings")]                    // whole words only
    [InlineData("Golden State Warriors win")]
    [InlineData("Federated Hermes buys stake")]
    [InlineData("How China is using summits to expand its global influence")]
    public void Title_WithoutKeyword_IsNotRelated(string title) =>
        Assert.False(GoldNews.IsRelated(title, extract: null));

    [Fact]
    public void Keyword_OnlyInExtract_IsRelated() =>
        Assert.True(GoldNews.IsRelated("Markets wrap", "The dollar fell after the Fed decision."));

    [Fact]
    public void Keyword_SplitOverLine_IsRelated() =>
        Assert.True(GoldNews.IsRelated("Markets wrap", "Bets on lower interest\nrates grew."));

    [Fact]
    public void NoKeyword_InEither_IsNotRelated() =>
        Assert.False(GoldNews.IsRelated("Markets wrap", "Stocks closed mixed."));
}
