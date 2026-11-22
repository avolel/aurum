using Aurum.App.SharedKernel.Entities;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Data.Entities.News;

/// <summary>
/// A gold-related headline from an RSS feed. Only these fields are kept (FR-3.7): no article body.
/// Item 6 reads the ones published in the hours before a move, to name causes and cite links.
/// </summary>
public class NewsArticle : AuditableEntity
{
    public long Id { get; set; }

    /// <summary>The feed's code in <c>NewsFeed</c>, e.g. <c>fxstreet</c>.</summary>
    public string Source { get; set; } = null!;

    public string Title { get; set; } = null!;

    /// <summary>The article's link. Unique: the only duplicate check, across all feeds.</summary>
    public string Url { get; set; } = null!;

    /// <summary>Plain text, HTML removed, at most <see cref="MaxExtractLength"/>. Null when the feed has none.</summary>
    public string? Extract { get; set; }

    /// <summary>The feed's publish time, in UTC.</summary>
    public DateTimeOffset PublishedAt { get; set; }
}
