using Aurum.App.Infrastructure.Pricing.News;
using Aurum.App.Logging;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Jobs;

/// <summary>
/// At startup and then every <c>News:Polling:Interval</c>, reads each <see cref="NewsFeed"/> and saves
/// its new gold-related items through <see cref="NewsImporter"/>.
/// </summary>
/// <remarks>
/// The only news writer, so <see cref="NewsImporter"/>'s load-then-insert stays safe. Feeds are read
/// one after another on this loop, never in parallel, for the same reason: two feeds sharing a link
/// would race on the unique index.
/// </remarks>
internal sealed class NewsPollingService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<NewsPollingOptions> options,
    IAppLogService<NewsPollingService> logger) : BackgroundService
{
    // Feeds whose last read had items but none usable. In memory: a restart warns once more, which is
    // fine. Only this service's one loop touches it, so it needs no lock.
    private readonly HashSet<string> _unreadable = [];

    internal enum UnreadableChange { None, Started, Recovered }

    /// <summary>
    /// Updates <paramref name="unreadable"/> for one read and says whether to log. A feed warns once when
    /// it becomes unreadable and stays quiet until a read has usable items again. An empty read isn't
    /// a recovery: a feed with nothing in it says nothing about its date format.
    /// </summary>
    internal static UnreadableChange Track(ISet<string> unreadable, NewsFeed feed, RssRead read)
    {
        if (read.NoneUsable)
        {
            return unreadable.Add(feed.Code) ? UnreadableChange.Started : UnreadableChange.None;
        }

        return read.Items.Count > 0 && unreadable.Remove(feed.Code) ? UnreadableChange.Recovered : UnreadableChange.None;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                foreach (var feed in NewsFeed.All)
                {
                    await TryAsync(feed, stoppingToken);
                }

                // From the end of a pass, so a slow feed delays the next pass instead of stacking passes.
                await Task.Delay(options.Value.Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // One try per feed: a broken feed or a failed save can't stop the others.
    private async Task TryAsync(NewsFeed feed, CancellationToken ct)
    {
        try
        {
            await RefreshAsync(feed, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await logger.LogErrorAsync("NewsPollingService", ex,
                details: $"Reading {feed.Code} failed; trying again next pass.", logType: LogType.SystemLog, ct: ct);
        }
    }

    private async Task RefreshAsync(NewsFeed feed, CancellationToken ct)
    {
        // A scope per feed: a failed save leaves tracked rows in the DbContext, and the next feed's
        // SaveChangesAsync would try to write them again.
        using var scope = scopeFactory.CreateScope();

        var read = await scope.ServiceProvider.GetRequiredService<RssClient>().GetAsync(feed, ct);
        switch (Track(_unreadable, feed, read))
        {
            case UnreadableChange.Started:
                await logger.LogAsync(LogType.SystemLog,
                    "NewsFeedUnreadable",
                    $"{feed.Code} had {read.ItemsInFeed} items and none were usable; its date format may have changed. Quiet until it recovers.",
                    level: LogLevel.Warning,
                    ct: ct);
                break;
            case UnreadableChange.Recovered:
                await logger.LogAsync(LogType.SystemLog,
                    "NewsFeedRecovered",
                    $"{feed.Code} has usable items again: {read.Items.Count} of {read.ItemsInFeed}.",
                    level: LogLevel.Information,
                    ct: ct);
                break;
        }

        var inserted = await scope.ServiceProvider.GetRequiredService<NewsImporter>().SaveAsync(feed, read.Items, ct);

        await logger.LogAsync(LogType.SystemLog,
            "RefreshedNewsFeed",
            $"{feed.Code}: {read.ItemsInFeed} in feed, {read.Items.Count} usable, {inserted} inserted.",
            level: LogLevel.Information,
            ct: ct);
    }
}
