namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// An RSS feed the news timer reads. <see cref="Code"/> is stored as <c>news_articles.Source</c>, so
/// renaming one splits a feed's history in two.
/// </summary>
public sealed record NewsFeed(string Code, Uri Url)
{
    // Checked 2026-11-21. Kitco (no feed, terms ban it) and MarketWatch (stale) are out; see D-29.
    public static readonly NewsFeed InvestingCommodities =
        new("investing-commodities", new Uri("https://www.investing.com/rss/commodities.rss"));

    public static readonly NewsFeed FxStreet =
        new("fxstreet", new Uri("https://www.fxstreet.com/rss/news"));

    public static readonly NewsFeed CnbcFinance =
        new("cnbc-finance", new Uri("https://www.cnbc.com/id/10000664/device/rss/rss.html"));

    public static readonly IReadOnlyList<NewsFeed> All = [InvestingCommodities, FxStreet, CnbcFinance];
}
