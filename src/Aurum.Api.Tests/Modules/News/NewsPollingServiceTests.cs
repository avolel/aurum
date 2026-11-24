using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.News;
using static Aurum.App.Infrastructure.Pricing.Jobs.NewsPollingService;

namespace Aurum.Api.Tests.Modules.News;

/// <summary>
/// The pure part of <see cref="NewsPollingService"/>: when the "feed unreadable" warning is logged.
/// Nothing drives the loop itself, as with <c>MacroPollingService</c>.
/// </summary>
public class NewsPollingServiceTests
{
    private static readonly RssItem Item = new("Gold hits record", "https://x.test/a", null, DateTimeOffset.UnixEpoch);

    private static readonly RssRead Unreadable = new(10, []);
    private static readonly RssRead Usable = new(10, [Item]);
    private static readonly RssRead Empty = new(0, []);

    [Fact]
    public void Warns_once_then_stays_quiet_while_unreadable()
    {
        var unreadable = new HashSet<string>();

        Assert.Equal(UnreadableChange.Started, Track(unreadable, NewsFeed.InvestingCommodities, Unreadable));
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.InvestingCommodities, Unreadable));
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.InvestingCommodities, Unreadable));
    }

    [Fact]
    public void Recovers_once_when_items_are_usable_again_then_can_warn_again()
    {
        var unreadable = new HashSet<string>();
        Track(unreadable, NewsFeed.InvestingCommodities, Unreadable);

        Assert.Equal(UnreadableChange.Recovered, Track(unreadable, NewsFeed.InvestingCommodities, Usable));
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.InvestingCommodities, Usable));
        Assert.Equal(UnreadableChange.Started, Track(unreadable, NewsFeed.InvestingCommodities, Unreadable));
    }

    [Fact]
    public void Empty_read_is_neither_a_warning_nor_a_recovery()
    {
        var unreadable = new HashSet<string>();
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.FxStreet, Empty));

        Track(unreadable, NewsFeed.FxStreet, Unreadable);
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.FxStreet, Empty));
        // Still unreadable: the empty read didn't clear it.
        Assert.Equal(UnreadableChange.Recovered, Track(unreadable, NewsFeed.FxStreet, Usable));
    }

    [Fact]
    public void Each_feed_is_tracked_on_its_own()
    {
        var unreadable = new HashSet<string>();
        Track(unreadable, NewsFeed.InvestingCommodities, Unreadable);

        Assert.Equal(UnreadableChange.Started, Track(unreadable, NewsFeed.CnbcFinance, Unreadable));
        Assert.Equal(UnreadableChange.None, Track(unreadable, NewsFeed.FxStreet, Usable));
    }

    [Fact]
    public void Usable_read_of_a_healthy_feed_logs_nothing() =>
        Assert.Equal(UnreadableChange.None, Track(new HashSet<string>(), NewsFeed.FxStreet, Usable));
}
