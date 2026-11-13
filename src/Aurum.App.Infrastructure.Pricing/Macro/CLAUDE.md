# The FRED client (D-22)

`FredClient` fetches one `FredSeries` from `fred/series/observations` and returns
`FredObservation`s, oldest first. Fetch only: nothing calls it or saves its rows yet. Registered in
`Program.cs` as a typed client.

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
  `DateTimeOffset?`. The save step has to pick the time of day (the release calendar).

# The BLS client (D-23)

`BlsClient` fetches one year of several `BlsSeries` in one `POST` to `timeseries/data/` and returns
`BlsObservation`s, newest month first within each series. Fetch only: the release calendar will call
it right after 08:30, for the month just released. Registered in `Program.cs` as a typed client.

- **Each `BlsSeries` names the `FredSeries` its row is stored under** (`Figure`). BLS writes the
  month's row under the FRED series; FRED's first published value replaces it later, logging a
  mismatch. The save step isn't built yet and must follow this.
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
