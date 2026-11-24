using System.Net;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing.News;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Modules.News;

/// <summary>
/// Parsing, skip rules and request shape for <see cref="RssClient"/>, with no network. The three
/// saved files are real answers from 2026-11-21.
/// </summary>
public class RssClientTests
{
    private static string Saved(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Modules", "News", name));

    /// <summary>A feed holding the given <c>&lt;item&gt;</c> bodies.</summary>
    private static string Feed(params string[] items) =>
        $"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>t</title>{string.Concat(items.Select(i => $"<item>{i}</item>"))}</channel></rss>";

    private const string GoodDate = "<pubDate>Fri, 20 Nov 2026 21:58:41 GMT</pubDate>";

    /// <summary>The usable items only; the tests of <see cref="RssRead.ItemsInFeed"/> call Parse directly.</summary>
    private static IReadOnlyList<RssItem> Items(string xml) => RssClient.Parse(xml).Items;

    [Fact]
    public void Parse_CountsSkippedItemsInTheFeed()
    {
        var read = RssClient.Parse(Feed("<title>Gold</title>" + GoodDate, "<title>Kept</title><link>https://x.test/kept</link>" + GoodDate));

        Assert.Equal(2, read.ItemsInFeed);
        Assert.Single(read.Items);
        Assert.False(read.NoneUsable);
    }

    [Fact]
    public void Parse_EveryItemUnreadable_IsNoneUsable()
    {
        // The case the timer warns about: a publisher changed its date format.
        var read = RssClient.Parse(Feed(
            "<title>Gold</title><link>https://x.test/a</link><pubDate>2026-11-20T19:26:00</pubDate>",
            "<title>Fed</title><link>https://x.test/b</link><pubDate>2026-11-20T19:27:00</pubDate>"));

        Assert.True(read.NoneUsable);
    }

    [Fact]
    public void Parse_EmptyFeed_IsNotNoneUsable() =>
        // A quiet feed is normal, not a warning.
        Assert.False(RssClient.Parse(Feed()).NoneUsable);

    [Fact]
    public void Parse_SavedInvestingCommodities_ReadsItsOwnDateFormat()
    {
        var items = Items(Saved("investing-commodities.xml"));

        Assert.Equal(10, items.Count);
        var first = items[0];
        Assert.Equal("Gold Price Crash Tests $3,500 as Fed Hike Bets Reach 97%", first.Title);
        Assert.Equal("https://www.investing.com/analysis/gold-price-crash-tests-3500-as-fed-hike-bets-reach-97-200692028", first.Url);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 19, 26, 0, TimeSpan.Zero), first.PublishedAt);
        // The feed has no <description>.
        Assert.All(items, i => Assert.Null(i.Extract));
    }

    [Fact]
    public void Parse_SavedFxStreet_ReadsCdataAndCutsLongExtracts()
    {
        var items = Items(Saved("fxstreet-news.xml"));

        Assert.Equal(30, items.Count);
        Assert.Equal("The Euro takes a priced-in hike into a week that belongs to American inflation", items[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 58, 41, TimeSpan.Zero), items[0].PublishedAt);
        Assert.All(items, i => Assert.InRange(i.Extract!.Length, 1, NewsArticleConstants.MaxExtractLength));
        // The file's longest extract is 494 characters, so the cut really ran.
        Assert.Contains(items, i => i.Extract!.Length > NewsArticleConstants.MaxExtractLength - 10);
    }

    [Fact]
    public void Parse_SavedCnbcFinance_ReadsItems()
    {
        var items = Items(Saved("cnbc-finance.xml"));

        Assert.Equal(30, items.Count);
        Assert.Equal("How China is using summits to expand its global influence", items[0].Title);
        Assert.Equal("https://www.cnbc.com/2026/11/21/china-xi-trump-putin-global-diplomacy-shenzhen.html", items[0].Url);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 6, 0, 2, TimeSpan.Zero), items[0].PublishedAt);
    }

    [Theory]
    [InlineData("investing-commodities.xml")]
    [InlineData("fxstreet-news.xml")]
    [InlineData("cnbc-finance.xml")]
    public void Parse_SavedFeed_EveryDateIsUtc(string file)
    {
        // Npgsql writes offset zero only; anything else throws on save.
        Assert.All(Items(Saved(file)), i => Assert.Equal(TimeSpan.Zero, i.PublishedAt.Offset));
    }

    [Theory]
    [InlineData("Fri, 20 Nov 2026 21:58:41 +0000")]
    [InlineData("Fri, 20 Nov 2026 16:58:41 -0500")]
    [InlineData("Fri, 20 Nov 2026 21:58:41 GMT")]
    public void Parse_OffsetDate_ComesBackAsUtc(string date)
    {
        var item = Assert.Single(Items(Feed($"<title>Gold</title><link>https://x.test/a</link><pubDate>{date}</pubDate>")));

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 21, 58, 41, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt.Offset);
    }

    [Theory]
    [InlineData("<title>Gold</title>" + GoodDate)]                                                // no link
    [InlineData("<title>Gold</title><link>  </link>" + GoodDate)]                                // blank link
    [InlineData("<link>https://x.test/a</link>" + GoodDate)]                                     // no title
    [InlineData("<title>Gold</title><link>https://x.test/a</link>")]                             // no date
    [InlineData("<title>Gold</title><link>https://x.test/a</link><pubDate>yesterday</pubDate>")] // unreadable date
    [InlineData("<title>Gold</title><link>https://x.test/a</link><pubDate>Nov 20, 2026 19:26</pubDate>")] // no zone
    public void Parse_UnusableItem_IsSkipped(string item)
    {
        // A good item beside it proves the skip is per item, not the whole feed.
        var items = Items(Feed(item, "<title>Kept</title><link>https://x.test/kept</link>" + GoodDate));

        Assert.Equal("Kept", Assert.Single(items).Title);
    }

    [Fact]
    public void Parse_LinkLongerThanTheColumn_IsSkipped()
    {
        var link = "https://x.test/" + new string('a', NewsArticleConstants.MaxUrlLength);

        Assert.Empty(Items(Feed($"<title>Gold</title><link>{link}</link>{GoodDate}")));
    }

    [Fact]
    public void Parse_HtmlExtract_IsPlainTextCutToTheColumn()
    {
        var words = string.Concat(Enumerable.Repeat("gold rallies ", 40));
        var description = $"<![CDATA[<p>Gold &amp; <b>silver</b></p>\n\n<a href=\"https://x.test\">{words}</a>]]>";

        var extract = Assert.Single(Items(Feed(
            $"<title>Gold</title><link>https://x.test/a</link>{GoodDate}<description>{description}</description>"))).Extract!;

        Assert.StartsWith("Gold & silver gold rallies", extract);
        Assert.DoesNotContain("<", extract);
        Assert.True(extract.Length <= NewsArticleConstants.MaxExtractLength);
    }

    [Fact]
    public void Parse_EmptyExtract_IsNull()
    {
        var item = Assert.Single(Items(Feed(
            $"<title>Gold</title><link>https://x.test/a</link>{GoodDate}<description><![CDATA[<p> </p>]]></description>")));

        Assert.Null(item.Extract);
    }

    [Fact]
    public void Parse_LongTitle_IsCutToTheColumn()
    {
        var title = new string('g', NewsArticleConstants.MaxTitleLength + 50);

        var item = Assert.Single(Items(Feed($"<title>{title}</title><link>https://x.test/a</link>{GoodDate}")));

        Assert.Equal(NewsArticleConstants.MaxTitleLength, item.Title.Length);
    }

    [Fact]
    public async Task GetAsync_SendsBrowserUserAgentToTheFeedUrl()
    {
        HttpRequestMessage? sent = null;
        var stub = new CountingHandler(r =>
        {
            sent = r;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Saved("fxstreet-news.xml")) };
        });

        var read = await new RssClient(new HttpClient(stub)).GetAsync(NewsFeed.FxStreet, CancellationToken.None);

        Assert.Equal((30, 30), (read.ItemsInFeed, read.Items.Count));
        Assert.Equal(NewsFeed.FxStreet.Url, sent!.RequestUri);
        Assert.Contains("Mozilla/5.0", sent.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetAsync_FailedStatus_Throws()
    {
        var stub = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => new RssClient(new HttpClient(stub)).GetAsync(NewsFeed.CnbcFinance, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains(NewsFeed.CnbcFinance.Code, ex.Message);
    }
}
