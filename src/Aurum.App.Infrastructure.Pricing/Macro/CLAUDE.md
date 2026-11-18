# The FRED client (D-22)

`FredClient` fetches one `FredSeries` from `fred/series/observations` and returns
`FredObservation`s, oldest first. `FredImporter` (below) saves them; `MacroPollingService` (at the
end) calls both. Registered in `Program.cs` as a typed client.

- **Core CPI (`CPILFESL`), payrolls (`PAYEMS`) and unemployment (`UNRATE`) are FRED series**: BLS's
  own API serves revised values only. FRED posts them 40+ minutes after BLS's 8:30, so on release
  morning `BlsClient` supplies the newest month (below, D-23). There is no FiscalData client: FRED
  already has the 10-year and 2-year yields.
- **Release series (`IsRelease`: CPI, PCE, core CPI, payrolls, unemployment) send `output_type=4` and
  the full real-time range, `1776-07-04` to `9999-12-31`.** Left at the default (today), FRED answers
  400 "No vintage dates exist", not an empty list. `ReleasedOn` is the row's `realtime_start`, the
  first publish date. Market series send neither and get a null `ReleasedOn`. `DFF` is a market
  series on purpose.
- **Not behind `QuotaHandler` or `IQuotaGovernor`.** FRED has no monthly allowance, only 120 requests
  a minute per key. It uses `AddStandardResilienceHandler` (retries 408, 429, 5xx; never 400),
  registered before `QueryKeyAuthHandler` so the key is added inside the retries, once.
- **`BaseUrl` must end in a slash.** The path is relative, so without it `/fred` is replaced.
- `"."` is a null `Value` and the row is kept. A `count` larger than the rows returned throws rather
  than dropping a second page. Numbers and dates parse with the invariant culture; the format and
  `"."` live in `SharedKernel/Constants/FredConstants.cs`.
- A blank `Macro:Fred:ApiKey` fails boot through `[Required]` + `ValidateOnStart` (D-9's rule).
  Nothing tests that directly.
- The exception for a failed status carries FRED's `error_message`. The client never sees the key,
  so it can't strip it; FRED is not known to echo it.
- `FredObservation.ReleasedOn` is a `DateOnly`; `MacroObservation.ReleasedAt` is a
  `DateTimeOffset?`. `FredImporter` picks the time of day (below).
- **`GetReleaseDatesAsync(release, from, ct)`** reads `fred/release/dates` from `realtime_start=from`.
  It sends `include_release_dates_with_no_data=true`, because without it FRED lists only dates that
  already have figures, so nothing in the future. FRED knows dates only to the end of the current year
  (checked 2026-11-16: asking from 2027-01-01 returns none), so January's dates appear when FRED adds
  them. The same `count` check applies.

# The releases (`FredRelease.cs`)

`FredRelease.All` lists CPI (FRED release 10), jobs (50) and PCE (54), each with its New York publish
time (08:30) and the series it carries. Ids checked against FRED on 2026-11-16. `At(day)` turns a
release date into the publish time in UTC; it is the only place that does that conversion.

- **Every release series is in exactly one release**, and no market series is in any.
  `FredRelease.Of(series)` throws otherwise, and `FredReleaseTests` pins it.
- **There is no Fed decision release.** FRED's release 101, "FOMC Press Release", is updated daily:
  it lists 318 dates in 2026, so its dates are not meeting days. Decision dates need another source.
- The `America/New_York` zone is looked up in a static field, so a host without tzdata throws
  `TypeInitializationException` on first use; `aspnet:10.0` has it (checked 2026-11-13).

# The FRED importer (D-23)

`FredImporter.SaveAsync(series, rows, ct)` saves one series' rows and returns `(Inserted, Updated)`.
Database only; the caller fetches. Registered scoped in `Program.cs`; `MacroPollingService` calls it.

- **Load, compare in C#, save through EF; not a raw upsert.** D-23's warning needs the old value,
  which Postgres 17 can't return from `ON CONFLICT DO UPDATE`. Safe only with one writer: one
  process, and FRED as the only macro writer. **A BLS save step on its own timer breaks that**; two
  writers could race on the same month's row. Reasoning in D-23.
- The `macro_series` row is created from `FredSeries` on first save. There is no migration seed, so
  `FredSeries.All` stays the only list. An existing row's title, units and frequency are never
  updated.
- Per row: new date → insert; same `Value` and `ReleasedAt` → skip; otherwise overwrite. **A changed
  `Value` on a release series logs a warning** with old and new values: that is where BLS's
  release-morning row gets replaced, and a first published value should otherwise never change.
  Market series are overwritten silently. A changed `ReleasedAt` alone overwrites without a warning.
- A null `Value` (FRED's `"."`) is stored as a row with null.
- `ReleasedAt` is the series' release time on `ReleasedOn` (`FredRelease.Of(series).At(day)`), as
  UTC, because Npgsql writes offset zero only. Today every release is 08:30 New York.
- **`SaveReleaseDatesAsync(release, dates, ct)`** adds the dates not yet in `macro_releases`, at the
  release's time, and returns how many it added. It never deletes, so a date FRED later moves stays
  behind as a row with no release. Rows are unique on `(ReleaseCode, ScheduledAt)`.
- Logs through `IAppLogService<FredImporter>` to `app_logs` (D-25). A changed release value is a
  `ReleaseValueChanged` row at `Warning`. `MacroPollingService` writes a `RefreshedFredSeries` row per
  series, and a failed series is an `Error` row with `LogType.SystemLog`.
- One `SaveChangesAsync` per call; `ApplyAuditFields` stamps the times from the injected
  `TimeProvider`.

# The BLS client (D-23)

`BlsClient` fetches one year of several `BlsSeries` in one `POST` to `timeseries/data/` and returns
`BlsObservation`s, newest month first within each series. Fetch only: the release calendar will call
it right after 08:30, for the month just released. Registered in `Program.cs` as a typed client.

- **Each `BlsSeries` names the `FredSeries` its row is stored under** (`Figure`). BLS writes the
  month's row under the FRED series; FRED's first published value replaces it later, logging a
  mismatch. `FredImporter` does FRED's half; BLS's save step isn't built yet and must follow this.
- **The key is in the `POST` body**, so there is no auth handler. **BLS copies a bad key into its
  refusal message**, so the client replaces the key with `[key]` in the answer before parsing or
  throwing. Don't move that line below anything that reads the body.
- **A refusal is HTTP 200** with `status` other than `REQUEST_SUCCEEDED`; `Parse` throws with BLS's
  `message`. A requested series missing from the answer throws.
- `year` is the figure's year: January's release of December figures passes the previous year.
- Period `M13` (annual average) is skipped; `"-"` is a null `Value` and the row is kept. Constants
  are in `SharedKernel/Constants/BlsConstants.cs`.
- **Not behind `QuotaHandler`**: 500 requests a day, 50 every 10 seconds, and the governor only knows
  months. `AddStandardResilienceHandler` retries a 429 up to 3 times. Daily use isn't counted.
- **`BaseUrl` must end in a slash** (`https://api.bls.gov/publicAPI/v2/`), same reason as FRED's.
- A blank `Macro:Bls:ApiKey` fails boot (D-9's rule).
- Not yet proven that BLS has the figure at 08:30 sharp; the 2026-12-04 run of
  `ops/bench/fred_delay.py` checks it.

# The macro timer (`Jobs/MacroPollingService.cs`)

A `BackgroundService` that runs once at startup, then every `Macro:Polling:RefreshInterval`
(6 hours). Each pass fetches every series in `FredSeries.All` and saves it through `FredImporter`.
Then, for each `FredRelease`, it fetches the dates from today (UTC) and adds the new ones to
`macro_releases`, with a `RefreshedFredReleaseDates` log row. Nothing reads the calendar yet; the
release-morning fetch (step 3) will.

- **It is the only macro writer** (D-23). BLS's release-morning save must be added inside it, not on
  a timer of its own, or two writers can race on the same month's row.
- **Where a fetch starts:** `FetchFrom` returns 60 days before the newest stored `ObservedOn`, so a
  late month or a corrected market figure is picked up. A series with no rows starts at
  `Macro:Polling:HistoryStart` (2006-01-01). That is the past-figure load: FRED returns 20 years in
  one request. `HistoryStart` is read only for an empty series, so changing it reloads nothing.
- **One scope, and one try, per series and per release.** A failed save leaves its rows tracked in
  the `DbContext`, and a shared context would try to write them again with the next one. A failure
  is logged and retried next pass; the others carry on.
- Both settings are checked at startup in `Program.cs`: a non-positive interval or a missing or
  future `HistoryStart` stops the app. They live in `appsettings.json` only, since neither is secret.
- `AurumApiFactory` removes it, so no test calls the real FRED.
