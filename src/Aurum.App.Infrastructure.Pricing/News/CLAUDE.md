# The news feed (D-29)

Phase 2 item 2: just enough news for item 6's explanations to cite. `NewsPollingService`
(`Jobs/`) reads three RSS feeds at startup and every `News:Polling:Interval` (15 minutes). It keeps
the gold-related items and stores them in `news_articles`. There's no endpoint; the news screen and
`/news` are Phase 3.

```
NewsPollingService ─┬─ RssClient.GetAsync(feed)      → RssRead (ItemsInFeed, Items)
                    └─ NewsImporter.SaveAsync(feed)   → GoldNews.IsRelated filter → news_articles
```

## The feeds (`NewsFeed.cs`)

`NewsFeed.All` is Investing.com commodities, FXStreet and CNBC Finance, checked 2026-11-21. Each
`Code` is stored as `news_articles.Source`, so renaming one splits that feed's history in two.

- **Kitco and MarketWatch are out** (D-29). Kitco has no feed, and its terms ban automated retrieval.
  MarketWatch's market feeds stopped updating in mid-2025.
- **The feeds' terms were read 2026-11-24 (D-29 quotes them).** Investing.com (§10(c)) and CNBC
  (Versant's Prohibited Actions, item 12) ban automated retrieval and copying even for personal use;
  FXStreet allows "personal and private use". All three are kept because the app has one user and
  won't be launched publicly. **Never expose `news_articles` to anyone else** (an endpoint other
  people can reach, a shared screen, a copy of the table) without written permission from each
  publisher, or replacement feeds.

## The reader (`RssClient.cs`)

Reads RSS 2.0 with `System.Xml.Linq`. `System.ServiceModel.Syndication` would be a package for four
fields. Registered in `Program.cs` as a typed client with `AddStandardResilienceHandler`.

- **No `QuotaHandler`**: the feeds have no allowance. **No `BaseAddress`**: each feed's absolute URL
  is the request.
- Sends a browser-style `User-Agent` on every request, the one the saved test files were fetched
  with. Some publishers answer 403 or an HTML page to clients that don't look like a browser; a
  request without it hasn't been tried against these three.
- A failed status throws `HttpRequestException` carrying the status and the feed's code.
- **Dates use three exact formats** (`DateFormats`), each naming its zone: RFC 822 with an offset
  (`zzz` also reads `+0000`), RFC 822 with `GMT`, and Investing.com's own `Nov 20, 2026 19:26 GMT`.
  **Not a general parse**: that reads a date with no zone as server-local time and stores it hours
  off. All come back as UTC, since Npgsql writes offset zero only. Zone names other than `GMT` (`EST`,
  `UT`) and dates without seconds in RFC 822 aren't read; no real item uses them.
- **An item is skipped** when it has no title, a missing or blank link, a link longer than the column,
  or no readable date. A long link is skipped, not cut: a cut link is a broken link the unique index
  would treat as a real one. Real links are about 130 characters.
- The extract has tags removed first, then HTML entities decoded. The other order turns escaped
  `&lt;b&gt;` text into a tag and removes it. Whitespace is collapsed, the text cut to 300 characters,
  and an empty extract becomes null. Titles are cut to 512.
- **The column lengths live in `SharedKernel/Constants/NewsArticleConstants.cs`.** Postgres rejects
  an over-long value, which fails the feed's whole save, not one row.
- `RssRead.ItemsInFeed` counts every `<item>`, skipped ones included. **`NoneUsable`** (items, but
  none usable) is how a changed date format shows up: without it, the feed looks quiet with no error.

## The filter (`GoldNews.cs`)

`IsRelated(title, extract)`: a whole-word, case-insensitive match in either field on gold, bullion,
precious metal(s), XAU, Fed, Federal Reserve, FOMC, interest rate(s), inflation, CPI, PCE, Treasury
yield(s), dollar or safe haven (also `safe-haven`). Phrases match across any whitespace.

- **It runs before saving** (D-29). A word added later never reaches an article already skipped;
  after 8 hours those are outside item 6's window anyway.
- `\b` keeps "Goldman" and "golden" out and lets "Gold's" and "XAU/USD" in. "Fed up" gets in.
- **"dollar" and "inflation" let in currency news.** On the saved files it keeps 19 of 30 FXStreet
  items. A narrower list was tried and turned down (D-29); item 6 can rank gold-named articles first.

## The importer (`NewsImporter.cs`)

`SaveAsync(feed, items, ct)` returns the number inserted. Database only; the caller fetches.
`internal`, registered scoped in `Program.cs`, the `FredImporter` pattern.

- Keeps the related items, **drops repeated links within the batch** (one repeat would break the
  unique index and fail the whole save), loads which links already exist in one query, adds the
  rest, saves once.
- **`Url` is unique across feeds.** A story two feeds carry is kept once, credited to the feed that
  saved it first, which follows `NewsFeed.All`'s order. Nothing reads the credit.
- **Never updates.** A republished article with a new title keeps its first one.
- **Safe only with one writer.** A second writer could insert the same link between the load and
  the save, and the unique index would fail that whole save. `NewsPollingService` is the only writer
  and reads the feeds one at a time.
- Links that differ only in case or a tracking query string are saved twice. Text fingerprints are
  Phase 3.

# The news timer (`Jobs/NewsPollingService.cs`)

A pass at startup, then the next one `Interval` after the pass **ends**, so a slow feed delays the
next pass instead of overlapping it. No calendar, unlike `MacroPollingService`.

- **One scope and one try per feed.** A failed save leaves rows tracked in the `DbContext`, and a
  shared one would try to write them again with the next feed. A failure is an `Error` row
  (`LogType.SystemLog`, action `NewsPollingService`); the feed is tried again next pass.
- Each feed logs `RefreshedNewsFeed`: items in the feed, usable, inserted.
- **`NewsFeedUnreadable` (Warning) is logged once** when a feed turns `NoneUsable`, then nothing
  until it has usable items again, which logs `NewsFeedRecovered`. An empty read is neither. A failed
  request doesn't change the state. `Track` holds the rule; `NewsPollingServiceTests` pins it.
- The unreadable set is in memory, so a restart warns once more for a feed still broken. Only the
  service's loop touches it, so it has no lock.
- **The interval is checked at startup**: zero or negative stops the app (a zero `Task.Delay` would
  re-read every feed in a tight loop). It lives in `appsettings.json` only; the feeds need no key, so
  there's no `.env` or compose line.
- `AurumApiFactory` removes it, so no test reads a real feed. Nothing drives the loop itself.
- Old articles aren't deleted. §11 keeps news 60 days; that's before-launch work.
