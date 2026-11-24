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
        if (read.NoneUsable)
        {
            await logger.LogAsync(LogType.SystemLog,
                "NewsFeedUnreadable",
                $"{feed.Code} had {read.ItemsInFeed} items and none were usable; its date format may have changed.",
                level: LogLevel.Warning,
                ct: ct);
        }

        var inserted = await scope.ServiceProvider.GetRequiredService<NewsImporter>().SaveAsync(feed, read.Items, ct);

        await logger.LogAsync(LogType.SystemLog,
            "RefreshedNewsFeed",
            $"{feed.Code}: {read.ItemsInFeed} in feed, {read.Items.Count} usable, {inserted} inserted.",
            level: LogLevel.Information,
            ct: ct);
    }
}
