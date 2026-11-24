namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// One usable feed item: every field already fits its <c>news_articles</c> column, and
/// <see cref="PublishedAt"/> is UTC (Npgsql writes offset zero only).
/// </summary>
public sealed record RssItem(string Title, string Url, string? Extract, DateTimeOffset PublishedAt);
