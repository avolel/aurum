# The FRED client (D-22)

`FredClient` fetches one `FredSeries` from `fred/series/observations` and returns
`FredObservation`s, oldest first. Fetch only: nothing calls it or saves its rows yet. Registered in
`Program.cs` as a typed client.

- **There is no BLS or FiscalData client.** Core CPI (`CPILFESL`), payrolls (`PAYEMS`) and
  unemployment (`UNRATE`) are FRED series: BLS's own API serves revised values only, and FRED already
  has the 10-year and 2-year yields. FRED posts after BLS's 8:30; how long after is not yet measured.
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
