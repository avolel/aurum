using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data.Entities.News;
using Aurum.App.Infrastructure.Pricing.News;
using Microsoft.EntityFrameworkCore;

namespace Aurum.Api.Tests.Modules.News;

/// <remarks>Each save uses a fresh <c>DbContext</c>, as each poll would.</remarks>
[Collection(PostgresCollection.Name)]
public class NewsImporterTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset Published = new(2026, 11, 20, 19, 26, 0, TimeSpan.Zero);

    private static RssItem Gold(string url, string title = "Gold hits record") =>
        new(title, url, "Bullion rose as the dollar fell.", Published);

    private static readonly RssItem Unrelated =
        new("How China is using summits to expand its global influence", "https://x.test/china", null, Published);

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.NewsArticles.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SaveAsync(NewsFeed feed, params RssItem[] items)
    {
        await using var db = fixture.CreateDbContext();
        return await new NewsImporter(db).SaveAsync(feed, items, Ct);
    }

    private async Task<List<NewsArticle>> StoredAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.NewsArticles.AsNoTracking().OrderBy(a => a.Url).ToListAsync(Ct);
    }

    [Fact]
    public async Task Inserts_new_links()
    {
        var inserted = await SaveAsync(NewsFeed.FxStreet, Gold("https://x.test/a"), Gold("https://x.test/b", "Fed holds rates"));

        Assert.Equal(2, inserted);
        var stored = await StoredAsync();
        Assert.Equal(["https://x.test/a", "https://x.test/b"], stored.Select(a => a.Url));
        var first = stored[0];
        Assert.Equal((NewsFeed.FxStreet.Code, "Gold hits record", "Bullion rose as the dollar fell.", Published),
            (first.Source, first.Title, first.Extract, first.PublishedAt));
        // Read back from Postgres, so this proves the stored time is UTC, not only the input.
        Assert.Equal(TimeSpan.Zero, first.PublishedAt.Offset);
    }

    [Fact]
    public async Task Second_save_of_same_items_inserts_nothing()
    {
        var items = new[] { Gold("https://x.test/a"), Gold("https://x.test/b") };
        await SaveAsync(NewsFeed.FxStreet, items);
        var before = await StoredAsync();

        var inserted = await SaveAsync(NewsFeed.FxStreet, items);

        Assert.Equal(0, inserted);
        // UpdatedAt unchanged proves no UPDATE ran, not only that the counter says so.
        Assert.Equal(before.Select(a => (a.Id, a.UpdatedAt)), (await StoredAsync()).Select(a => (a.Id, a.UpdatedAt)));
    }

    [Fact]
    public async Task Link_saved_by_another_feed_is_not_repeated()
    {
        await SaveAsync(NewsFeed.FxStreet, Gold("https://x.test/shared"));

        var inserted = await SaveAsync(NewsFeed.CnbcFinance, Gold("https://x.test/shared"), Gold("https://x.test/cnbc-only"));

        Assert.Equal(1, inserted);
        var stored = await StoredAsync();
        // The first feed to save a link keeps it.
        Assert.Equal(NewsFeed.FxStreet.Code, Assert.Single(stored, a => a.Url == "https://x.test/shared").Source);
        Assert.Equal(NewsFeed.CnbcFinance.Code, Assert.Single(stored, a => a.Url == "https://x.test/cnbc-only").Source);
    }

    [Fact]
    public async Task Unrelated_item_is_not_saved()
    {
        var inserted = await SaveAsync(NewsFeed.CnbcFinance, Unrelated, Gold("https://x.test/a"));

        Assert.Equal(1, inserted);
        Assert.Equal("https://x.test/a", Assert.Single(await StoredAsync()).Url);
    }

    [Fact]
    public async Task Same_link_twice_in_one_feed_is_saved_once()
    {
        // Without de-duplication the unique index fails the whole save, losing every item.
        var inserted = await SaveAsync(NewsFeed.FxStreet,
            Gold("https://x.test/a"), Gold("https://x.test/a", "Gold hits record (updated)"), Gold("https://x.test/b"));

        Assert.Equal(2, inserted);
        Assert.Equal("Gold hits record", Assert.Single(await StoredAsync(), a => a.Url == "https://x.test/a").Title);
    }

    [Fact]
    public async Task Nothing_related_saves_nothing()
    {
        Assert.Equal(0, await SaveAsync(NewsFeed.CnbcFinance, Unrelated));
        Assert.Empty(await StoredAsync());
    }
}
