using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// Reads one RSS 2.0 feed and returns its usable items, in feed order, with how many items it held.
/// Fetch only; the caller filters and saves. An item with no title, no link, a link too long to store, or no readable date is skipped.
/// </summary>
/// <remarks>
/// <c>System.Xml.Linq</c>, not <c>System.ServiceModel.Syndication</c>: four fields from RSS 2.0 don't
/// justify a package.
/// </remarks>
public partial class RssClient(HttpClient http)
{
    // Some publishers answer 403 or an HTML page to a client that doesn't look like a browser.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    // Exact formats, not a general parse: a general parse reads a date with no zone as server-local
    // time and stores it hours off. Every format here names its zone, so such a date is skipped.
    private static readonly string[] DateFormats =
    [
        "ddd, d MMM yyyy HH:mm:ss zzz",   // RFC 822 with an offset; zzz also reads "+0000"
        "ddd, d MMM yyyy HH:mm:ss 'GMT'", // FXStreet, CNBC
        "MMM d, yyyy HH:mm 'GMT'",        // Investing.com: not RFC 822 at all
    ];

    public async Task<RssRead> GetAsync(NewsFeed feed, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{feed.Code} answered {(int)response.StatusCode}.", inner: null, response.StatusCode);
        }

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Pure, so parsing and the skip rules test without HTTP.</summary>
    internal static RssRead Parse(string xml)
    {
        var all = XDocument.Parse(xml).Descendants("item").ToList();
        var items = new List<RssItem>();
        foreach (var item in all)
        {
            var title = Clean((string?)item.Element("title"));
            var link = ((string?)item.Element("link"))?.Trim();

            // A cut link is a broken link the unique index would treat as a real one, so skip instead.
            // Real links are ~130 characters (checked 2026-11-21).
            if (title.Length == 0 || string.IsNullOrEmpty(link) || link.Length > NewsArticleConstants.MaxUrlLength
                || !TryParseDate((string?)item.Element("pubDate"), out var publishedAt))
            {
                continue;
            }

            var extract = Clean(StripHtml((string?)item.Element("description")));
            items.Add(new RssItem(
                Cut(title, NewsArticleConstants.MaxTitleLength),
                link,
                extract.Length == 0 ? null : Cut(extract, NewsArticleConstants.MaxExtractLength),
                publishedAt));
        }

        return new RssRead(all.Count, items);
    }

    private static bool TryParseDate(string? text, out DateTimeOffset utc) =>
        // AssumeUniversal makes the 'GMT' literal formats offset zero; AdjustToUniversal moves "-0500" to UTC.
        DateTimeOffset.TryParseExact(
            text?.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    // Tags out, then entities decoded: decoding first would turn "&lt;b&gt;" text into a tag and remove it.
    private static string StripHtml(string? html) =>
        html is null ? "" : WebUtility.HtmlDecode(HtmlTag().Replace(html, " "));

    private static string Clean(string? text) =>
        text is null ? "" : Whitespace().Replace(text, " ").Trim();

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
