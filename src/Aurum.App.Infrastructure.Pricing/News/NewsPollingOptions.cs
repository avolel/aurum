namespace Aurum.App.Infrastructure.Pricing.News;

public sealed class NewsPollingOptions
{
    public const string SectionName = "News:Polling";

    // 15 minutes: Investing.com keeps only 10 items, which can cover under an hour on a busy day,
    // so this leaves a 4x margin before an article falls off the feed unread (D-29).
    public TimeSpan Interval { get; set; }
}
