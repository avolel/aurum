using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.News;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// Saves one feed's gold-related items into <c>news_articles</c>. Database only; the caller fetches.
/// </summary>
/// <remarks>
/// Loads which links already exist in one query, adds the rest and saves once: the
/// <c>FredImporter</c> pattern. Safe only with one writer, which holds while the app is single-instance
/// and the news timer is the only writer. A second writer could insert the same link between the load
/// and the save, and the unique index would then fail that whole save.
/// </remarks>
internal sealed class NewsImporter(AurumDbContext db)
{
    /// <returns>Articles inserted, for the caller's log line.</returns>
    public async Task<int> SaveAsync(NewsFeed feed, IReadOnlyList<RssItem> items, CancellationToken ct)
    {
        // DistinctBy: a feed listing one link twice would otherwise break the unique index and fail
        // the whole save. Ordinal, as Postgres compares varchar.
        var related = items
            .Where(i => GoldNews.IsRelated(i.Title, i.Extract))
            .DistinctBy(i => i.Url, StringComparer.Ordinal)
            .ToList();

        if (related.Count == 0)
        {
            return 0;
        }

        // Across all feeds: a story two feeds share is kept once, under the first feed that saved it.
        var links = related.Select(i => i.Url).ToList();
        var existing = (await db.NewsArticles
                .Where(a => links.Contains(a.Url))
                .Select(a => a.Url)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var added = related.Where(i => !existing.Contains(i.Url)).ToList();
        db.NewsArticles.AddRange(added.Select(i => new NewsArticle
        {
            Source = feed.Code,
            Title = i.Title,
            Url = i.Url,
            Extract = i.Extract,
            PublishedAt = i.PublishedAt,
        }));

        await db.SaveChangesAsync(ct);
        return added.Count;
    }
}
