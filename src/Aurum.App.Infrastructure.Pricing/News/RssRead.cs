namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// One read of a feed. <see cref="ItemsInFeed"/> counts every <c>&lt;item&gt;</c>, skipped ones
/// included, so the timer can tell an empty feed from one whose items all failed to parse.
/// </summary>
public sealed record RssRead(int ItemsInFeed, IReadOnlyList<RssItem> Items)
{
    /// <summary>
    /// The feed had items and none were usable: most likely a changed date format, which would
    /// otherwise look like a quiet feed (0 fetched) with no error.
    /// </summary>
    public bool NoneUsable => ItemsInFeed > 0 && Items.Count == 0;
}
