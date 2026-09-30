# Phase 1 — the server side

This is my list of the remaining Phase 1 work. The groundwork is in place and its tests pass. See
`ops/decisions/decisions.md` for what's already decided and what still hasn't been checked.

The items are in an order where each one can be checked when it's finished, following the Phase 1
plan. Items 2 to 9 are a strict chain: each one builds on the one above, so they can't be reordered or
done at the same time. Items 0 and 10 stand alone and can be done whenever there's a gap.

**The goal:** `docker compose up` produces an API that serves a live gold price from three services,
falls back to the next service when one fails, and flags big price moves. It sends prices both over a
live connection (SignalR, which pushes updates to the app) and through ordinary web requests.

**This is server-side only.** The dashboard app, and its goal of showing the first content in under
1.5 seconds, is a separate batch of work, planned once the API is settled.

Decisions get written into `ops/decisions/decisions.md` **as each item is finished**, not all at once
at the end. A decision written up afterwards is a decision pieced back together from memory, and the
options I turned down, which are the part worth keeping, are exactly what gets lost.

A few terms used below:

- **Poll**: one scheduled "go get the gold price" check.
- **Lease**: one request taken from a service's monthly allowance.
- **Primary**: the service tried first. **Backups** are tried after it.
- **Request counter**: `PostgresQuotaGovernor`, which the code calls the "quota governor".
- **Circuit breaker**: the part that stops calling a service after several failures in a row, and
  tries again after a rest.

> **Decision numbers.** D-9 to D-15 are taken and written up:
>
> | Number | Decision | Item |
> |---|---|---|
> | D-9 | looking up each service's settings, and checking nested settings | 1 |
> | D-10 | where the check interval lives | 2 |
> | D-11 | three free services | 2 |
> | D-12 | finding services as a sorted list, not by name | 2 |
> | D-13 | where the per-try time limit lives | 3 |
> | D-14 | my own circuit breaker | 3 |
> | D-15 | the startup check counts tries | 4 (moved out of 3) |
>
> Numbers are handed out when a decision is actually made, not held back for planned work. So the
> Phase 1 plan's draft list is now off by **four**. The numbers used below are the real ones.
>
> How it drifted: item 2 briefly had two decisions both labelled D-10. The interval decision kept it,
> and the other two became D-11 and D-12, pushing items 3 to 8 down by two. The time limit decision
> then took D-13, which item 3's breaker had been holding, pushing items 5 to 8 down one more. Then
> the "count tries" setting moved out of item 3 took D-15 when it was actually done. So the "no
> answer, not zero" rule for the price-move feature is now **D-16**, and items 5 to 7 use **D-16 to
> D-18**. D-11's two references to it were updated in place.

> **Since D-5 (2026-09-26):** every service is now set up in `src/Aurum.Api/Program.cs`. The
> `PricingModule`, `AddPricingModule`, `AddRealtimeModule` and `Add<Module>Module` methods mentioned in
> items 2, 3, 7 and 8 no longer exist. Items 7 and 8 were planned around them and need replanning
> before they start. Their notes below are left as originally written, so the plan's reasoning isn't
> lost.

---

## 0. Carried over from the groundwork: real prices on a schedule · 1 hour · must pass

The one thing standing between "the code is right" and "the code works against the real service". It
doesn't depend on anything below, so do it as soon as there's a real API key.

- [x] Put a real API key in `.env` and run `docker compose up`.
- [x] `SELECT * FROM price_ticks ORDER BY "ObservedAt" DESC LIMIT 10;` shows real prices, a sensible
      gap between when the price was taken (`ObservedAt`) and when the app got it (`ReceivedAt`),
      `Symbol = 'XAUUSD'` and `SourceCode = 'goldapi.io'`.
- [x] `SELECT * FROM price_sources;` shows `LastSuccessAt` moving forward.
- [x] Compare the middle price against a public gold price. `PriceQuote.Normalize` prefers GoldAPI's
      own `price` field over working out the halfway point between the buy and sell prices. Confirm
      that's the number I actually want on the chart.

      Checked 2026-09-07: `Mid` differs from the halfway point by at most $0.16 on a price of about
      $4,400 (0.004%). So GoldAPI's `price` *is* the halfway point, and preferring it changes nothing
      for this service.

      The comparison did turn up the problem it was written to find, just in a different place. The
      public site shows its **sell** price as the headline number, with a gap of about $14.50 between
      buy and sell, where GoldAPI's gap is about $0.83. The middle price can be compared across the
      two. The buy and sell prices can't. Item 2 puts three services into the same columns, so decide
      there whether buy and sell prices can be compared across services at all, or only ever read per
      service.

Note that `appsettings.json` now ships every `ApiKey` blank on purpose. A missing key stops the app at
startup, instead of the app spending the month being told "not authorised" (HTTP 401) one request at a
time. That's D-9 working, not a setup mistake.

---

## 1. Reshape the service settings, fix the settings check, fix the counter's lookup · DONE

`PriceSourcesOptions` is now a list of services looked up by their code (`SourceCode`).
`PriceSourcesOptionsValidator` checks the rules inside each entry, which the standard .NET check
(`ValidateDataAnnotations()`) never looked at.

- [x] **`GetCurrentPeriod` always finds real settings or stops.** The `switch` that fell back to a
      built-in "100 requests, resetting monthly" is gone. A service with no settings now throws an
      error. Test: `An_unconfigured_source_throws_rather_than_defaulting`.
- [x] **The rules inside each entry actually run.** `ApiKey`'s `[Required]` and
      `MonthlyRequestLimit`'s `[Range]` had never run. Test: `A_missing_api_key_fails_the_host_at_boot`.
- [x] **Checks that compare settings with each other moved to startup**: the "can the allowance pay
      for the schedule" check moved out of `PricePollingService.GuardPollBudget`, plus the rule that a
      30-day service (`RollingThirtyDays`) needs a start date (`PeriodAnchor`).
- [x] **D-9 written up**, with the options I turned down: keep the `switch` and add a case; use the
      service code as the list key; the .NET settings code generator.

~~Still open from this item~~, **closed in item 2.** `RequireByCode` threw from inside
`GoldApiIoSource`'s and `PricePollingService`'s field setup, meaning deep inside the app's setup, not
during the startup checks. `AddPriceSource<T>` now leaves a `RegisteredPriceSource` marker for each
service, and `PriceSourcesOptionsValidator` compares those markers with the services in the settings.
So a service set up in code with no settings stops the app at startup with an error naming it. The
`RequireByCode` error is now only a backup.

---

## 2. Three services + one shared setup helper · DONE · needed by 3

Three free services, so the combined schedule is usable at no cost and the fallback list has something
real to fall back to. Each service has its own row in the request counter.

- [x] `ApiNinjasSource`: `/v1/goldprice` returns `{name, price, updated}`. One price only, which
      `PriceQuote.Normalize` already handles. **Gold only: refuse any symbol other than `XAUUSD`**,
      instead of returning gold for whatever was asked.
- [x] `MetalpriceApiSource`: two traps, both silent:
      - It takes the API key **in the web address**. So the web address must never appear in a
        `PriceSourceException` message or a retry log, or the key leaks.
      - `/v1/latest?base=USD&currencies=XAU` returns *ounces per dollar* (about 0.00042). `Mid` is
        stored with 4 decimal places, so that becomes 0.0004 with no error, and the price-move feature
        would see a $2,300 crash. Ask for `base=XAU&currencies=USD` instead, **and** reject any
        middle price outside a sensible range before returning it.
- [x] `AddPriceSource<T>` shared setup helper, so the order of the request steps from item 3 can't
      be got wrong for one service. *(Originally a private method in `PricingModule`. Since D-5 it's
      `Program.AddPriceSource<T>` in `Program.cs`.)*
- [x] Services are found as a list (`IEnumerable<IPriceSource>`), sorted by `Priority`.
- [x] Schedule check: **the primary has to afford the worst case, backups don't.**
      - One interval for the whole feed: `PricePolling:PollInterval`. The switched-on service with the
        lowest `Priority` must afford it for a full month.
      - Making every service afford "this service handles every check" sounds like the safe rule. But
        it ties the schedule to the smallest allowance: GoldAPI's 100 a month would limit the whole
        feed to one check every 22 hours 19 minutes, however much room the others have.
      - A backup that runs out during an outage is counted and cut off by the request counter.
        `PricePollingService` logs, at startup, how many days each backup could cover alone, so the
        risk is visible.
      - Two services tied for the lowest `Priority`, or no services switched on, stop the app at
        startup.
      - **D-10 written up.** The check counts *requests*, not checks. Item 4's `MaxAttempts`
        multiplier (D-15) is what makes that true.
- [x] The settings checker confirms every service set up in code has settings (carried over from item
      1). `AddPriceSource<T>` leaves a `RegisteredPriceSource` marker, and the checker compares those
      with the services in the settings. So the error happens during the startup checks, not deep in
      setup.
- [x] `appsettings.json`, `.env.example` and `docker-compose.yml` include the two new services. Note
      that `docker-compose.yml` only lets you override `MonthlyRequestLimit` for GoldAPI, because it's
      the one limit expected to change, when I move to the paid plan (D-8). The other two take their
      limits from `appsettings.json`.
- [x] **Database change adding the two new rows to `price_sources`.** `price_ticks.SourceCode` must
      match a row in `price_sources`. Without the new rows, the first switch to an unlisted service
      would have failed with database error `23503` when saving, *after* the request was sent and
      counted. Done in `20260919102443_SeedAdditionalPriceSources`, using the same "insert, or do
      nothing if it's already there" statement as `InitialSchema`. This used to be listed only under
      item 6.
- [x] **D-11 written up**: three free services instead of the paid plan, and what that still doesn't
      buy. API Ninjas is the only one with real room to spare, and it only does gold, so it can't
      serve Phase 6's other-metals work.
- [x] **D-12 written up**: services found as a sorted list, not looked up one by one by name. Looking
      up by name would force the fallback list to keep its own list of names, repeating what
      `Priority` already says.

---

## 3. The fallback list + circuit breaker · 1 to 2 days · FR-1.3 · needed by 4

`IPriceFeed` is **its own interface**, not another `IPriceSource` that wraps the others. A wrapper
would end up in the same list of services it's reading from. And its result has to report which
services were tried (`AttemptedSources`) and whether a backup was used (`UsedFallback`), which the
single-service interface shouldn't have to carry.

- [x] Add `Microsoft.Extensions.Http.Resilience` (the Polly-based retry setup). Register the retry
      steps **before** `QuotaHandler`, giving the order: retries, then counter, then the actual
      request. That's what makes "the counter sees exactly what leaves the app" guaranteed by the
      setup, not by everyone remembering.
- [x] The retry rule (`ShouldHandle`) **leaves out** `QuotaExhaustedException`. The service is
      healthy, just out of requests, so retrying would try to spend requests that were already
      refused.
- [x] What each error means, each one different:
      - `QuotaExhaustedException`: never retried, and **not** a breaker failure. Move on right away.
      - Network error, timeout, or an error status code: retried by the retry steps, which are the
        only place that can see them. If the last try fails, it counts as a breaker failure.
      - `PriceSourceException`: **not** in the retry rule, because the retry steps can't see it. Every
        service reads the price out of the answer after the request steps have finished. So this error
        is raised after the request has already returned and been judged a success, by which point
        the retries are already used up. It only causes a fall back to the next service and a breaker
        failure, both counted by `FailoverPriceFeed`, which sits above the services and does see it.
        Adding it to the retry rule does nothing and suggests protection the retry steps don't have.
      - Every service down: `AllSourcesFailedException`, carrying what happened with each service.
- [x] The timer waits **only when every service is out of requests**, and then only until the
      earliest reset (`ResetsAt`). The old single-service wait would have stopped the whole timer for
      a month.
- [x] My own circuit breaker in `FailoverPriceFeed`, not Polly's.
- [x] The breaker's state lives in memory and is the real answer. `PriceSource.LastFailureAt` and
      `LastFailureReason` are a copy for people to look at, written once per check. **Don't reload the
      state at startup.** A freshly started app should try every service again. Say so in the notes
      on the class, or it looks like an oversight next to the request counter's "always save" rule.
- [x] `Each_retry_attempt_spends_its_own_lease`: a fake service answers "server error" (HTTP 500)
      three times with three tries allowed; check that the count is 3. That's the test form of "the
      counter sits beneath the retries", which was otherwise only guaranteed by the order of two lines
      of setup (then in `PricingModule.cs`, now in `Program.cs`).
- [x] `Quota_exhaustion_is_not_retried`, split into two:
      - `A_provider_rejection_is_not_retried`: the service answers "too many requests" (HTTP 429).
        Exactly one call is made, and `ProviderRejectedAt` is set.
      - `A_denied_lease_never_reaches_the_network`: the month's row starts already at its limit. No
        calls are made and the count doesn't change. A refusal must not spend a request just to find
        out it's a refusal.
- [x] `Open_circuit_source_is_not_called`: checks that the call count is 0, not only the result.
- [x] **D-14 written up**: why the breaker is my own.
      - `GoldApiIoSource` throws `PriceSourceException` *after* an "OK" answer when the content is
        rubbish. So Polly's breaker would never trip on a service that's falling apart, while the
        fallback list spent a request every check.
      - Polly's breaker measures its rest period on the real clock. That breaks the rule that the
        `TimeProvider` handed in is the only clock, and means the breaker can't be tested with a fake
        clock.

**Finished 2026-09-26.** `FailoverPriceFeed`, `SourceCircuit`, `SourceCircuitStore`, the rewritten
`PricePollingService`, and 25 new tests. Two things turned up along the way that weren't in the plan:

- **The retry rule never retried an error status code.** It only listed two kinds of *thrown* errors
  (a timeout and a network error). But the web client *returns* a "server error" answer, it doesn't
  throw one. So a service answering "unavailable" (HTTP 503) all day was never retried at all. The
  retry setting looked right and did nothing on the most common failure it exists for. Fixed by also
  retrying on the answers "request timeout" (HTTP 408) and any server error (HTTP 500 and up).
  `Each_retry_attempt_spends_its_own_lease` caught it on its first run, which is the whole argument for
  that test.
- **The retry setup (`AddResilienceHandler`) takes its clock from the app's setup** and hands it to
  Polly. Setting up a fake clock there makes the wait between tries wait on a clock nothing moves
  forward, so the test hangs instead of failing. Noted in the notes on `ResilienceWiringTests`.

Moved out of this item and **since finished** (see item 4 below): the
`PriceFeed:Resilience:MaxAttempts` setting and the schedule check's multiplier. The number of retries
was a built-in 2 from D-13, so the allowance check undercounted the worst case by three times. Keeping
it out of this batch of changes was the right call. It put a change that multiplies spending into its
own reviewable batch, and the 3x turned out to need an interval change and a time limit change to fit.

---

## 4. Saved latest price · half a day · needed by 8

### Carried over from item 3: the number-of-tries setting · **finished 2026-09-26**

- [x] `PriceFeed:Resilience:MaxAttempts` (default 3) and `RetryBackoffBase` (default 2 seconds, the
      starting wait between tries). `Program.AddPriceSource` gives Polly `MaxAttempts - 1`, because
      Polly counts retries, not tries. That conversion happens there and nowhere else, so the settings
      checker never has to deal with the off-by-one.
- [x] `ValidatePollBudget` multiplies checks per month by `MaxAttempts`. **The circuit breaker doesn't
      limit this.** `SourceCircuit` sees whether each check worked in the end, and retries happen
      beneath it. So a service that fails twice and works on the third try costs three times as much
      forever, while looking healthy. During a *long outage* the breaker very nearly cancels the
      extra cost out, which is why this looked limited when it wasn't.
- [x] New rule: `TotalTimeout` must leave room for `MaxAttempts` × `RequestTimeout`, plus the waits
      between tries (`MinimumTotalTimeout`). All three services shipped with 35 seconds, where three
      10-second tries with a 2-second starting wait need 36. So the last try ran on 9 seconds of its
      10 and was still counted.
- [x] Interval 5 minutes → 15 minutes, and `TotalTimeout` 35 seconds → 40, both forced by the two
      checks above. Turned down `MaxAttempts: 1` to keep 5 minutes: every network hiccup would then
      become a switch to `goldapi.io` and its 100 requests a month, which a 1% failure rate would use
      up entirely. **D-15 written up.**
- [x] Tests: the check rejects 186 requests from 62 checks, and accepts the same interval at one try.
      The time limit rule rejects 30 and 35 seconds, and accepts 35 seconds at one try.
      `Shipped_total_timeouts_fit_the_shipped_attempt_count` checks the real settings file against the
      checker's own formula. At the time, 82 tests passed.
- [x] `Backoff_schedule_matches_what_MinimumTotalTimeout_models` in `ResilienceWiringTests` measures the
      real waits between tries against the formula. **It failed on its first run and found a real
      bug.** The web retry options (`HttpRetryStrategyOptions`) make the waits random by default
      (`UseJitter` is `true`), unlike Polly's basic options. So the formula assumed fixed waits while
      the real retries used random ones, and randomness makes the waits *longer* (503 milliseconds
      measured against an expected 400). The formula was underestimating. Random waits are now
      switched off on purpose (`UseJitter = false`). They exist to spread out a crowd of programs
      retrying at once, and there's only one timer. The note this replaced said the risk might happen.
      It was already happening.
- [ ] **Still not covered:**
      - The formula is only tested at one starting wait and three tries. When it fails, it says the two
        disagree without saying which one changed.
      - `BuildProvider` in the tests now sets `RetryBackoffBase` to 1 millisecond, so tests that only
        count requests stop spending six real seconds waiting. That means only this one test would
        notice if the waits between tries went wrong.

### The saved price itself

- [ ] Keep the latest price per symbol in one shared, thread-safe dictionary
      (`ConcurrentDictionary<symbol, LatestQuote>`), holding a record that never changes after it's
      created. Swapping in a new price is then one step that can't be half-done.
- [ ] **Not .NET's built-in memory cache (`IMemoryCache`).** It throws entries away after a set time,
      which is exactly wrong when checks can be hours apart. Whether a price is fresh has to be
      *worked out when it's read*, using the app's clock, not enforced by throwing it away.
- [ ] Holds the price, whether a backup supplied it (`IsFallback`), and which services were tried
      (`AttemptedSources`). Offers `Age` and `IsStale`, measured against a setting that defaults to
      twice `PollInterval`. That's what makes the goal in §8 ("price visible within twice the check
      interval") something you can actually check, not just a hope.
- [ ] **Reload at startup** from the newest price per symbol in the database, sharing item 5's startup
      read. Otherwise a restart leaves `/v1/price/live` empty for up to a full check interval.
- [ ] After each check, the timer does things in this order: **save in memory → save to the database →
      send to connected apps.** A database hiccup must not hide a price the app successfully fetched.
      The in-memory copy isn't the official record.

---

## 5. How much did the price move · 2 to 3 days · the most important piece · FR-1.4 · needed by 6

**The rule, word for word in the notes on the class:**

> A price move over a time window is either worked out from two real prices, spaced close enough to
> that window's length, or it is **no answer** (null). The feature never makes up a starting price:
> no filling with zero, no guessing between two prices, no reusing an older price. "No answer" means
> "I don't know", and everything that shows it must show it as unknown, not as zero.

With hours between prices, the 1-minute, 5-minute and 15-minute windows hold one price each, and
"latest minus the oldest in the window" gives 0.00%. That isn't "the price didn't move". It's "there's
no data". Mixing the two up is the purest form of the risk in §14, "delayed data misleading users".

- [ ] **One ring buffer per symbol**, meaning a fixed-size list that overwrites its oldest entry when
      full. Size it for the longest window (1 day) and serve all six windows from it, finding prices by
      binary search (repeatedly halving the range, which works because the list is in time order). Not
      one buffer per window: that stores each price six times, keeps six cut-off points in step, and
      hands back the *oldest price still inside the window*, which is the wrong starting price.
- [ ] Each entry is a small fixed value holding the time, the middle price and a number for which
      service it came from:
      `readonly struct Sample { DateTimeOffset ObservedAt; decimal Mid; byte SourceOrdinal; }`, kept in
      an array with a start position and a count. The size limit comes from a setting
      (`MaxSamplesPerSymbol`, default 8,640: 24 hours at one price every 10 seconds, about 200 KB per
      symbol). The size is limited by design, not by hoping.
- [ ] **The starting price is the last one at or before the window start**, not the first one after
      it. Find the last price at or before "now minus the window". Using the first one after would
      quietly *shrink* the window: with prices 8 hours apart, a "1 hour move" would really be measured
      over 8 hours and still be labelled 1 hour. That's mislabelling instead of declining to answer.
- [ ] No answer when:
      - the gap between the window start and the starting price is bigger than the allowed slack
        (`Tolerance(W)`, default half the window);
      - there are fewer than two prices;
      - every price is newer than the window start.
- [ ] **Prices that arrive out of order: drop them, never re-sort.** Being in time order *is* what
      makes binary search work. Drop any price whose time is at or before the last one, log it at debug
      level, and **add one to a counter people can see**. From the outside, "the price stopped moving"
      and "the app is dropping every price" look the same. Re-sorting would give a list that's in order
      but isn't what really happened.
- [ ] **Different services quoting slightly different prices: the most likely cause of false alarms.**
      Services disagree on the price of gold by a few dollars. So switching to a backup between checks
      looks like the price moved by exactly that amount. At a threshold of 0.25% in 5 minutes, that
      invents a big move that Phase 2 will then confidently explain. Each price records which service
      it came from, and a window whose starting and latest prices came from different services is
      flagged `CrossSource`. **Write in D-16 that this reduces the problem but doesn't fix it.** The fix
      is a correction per service, which needs data I don't have yet.
- [ ] **Measuring how jumpy the price is (volatility) from three prices means nothing.** Require at
      least `MinSamplesForVolatility` prices (default 5), or report `Volatility = null`. Then the
      classifier's volatility rule simply *doesn't apply*, instead of giving a nonsense answer.
- [ ] Reloading at startup reads
      `WHERE Symbol = @s AND ObservedAt >= now - LongestWindow ORDER BY ObservedAt DESC LIMIT @capacity`,
      newest first, then flips the order in memory. Newest first matches the existing index on
      `(Symbol ASC, ObservedAt DESC)`, so the database can use it.
- [ ] Startup loading is a method, **`EnsureWarmAsync(ct)`, safe to call more than once**. The timer
      waits for it before its first check, and so do the endpoints on their first read. Not a separate
      background service: "background services start in the order they're set up" is a rule nobody
      re-reads before adding a new one, and breaking it shows up as an empty first result that looks
      like a normal fresh start.
- [ ] `DeltaEngine` is created once for the whole app, and gets a way to create a fresh database
      connection for the startup read (`IServiceScopeFactory`). Same reasoning as `QuotaHandler`:
      something that lives for the whole app must not hold on to a database connection meant for one
      unit of work.
- [ ] Tests: `Window_with_no_bracketing_sample_is_null_not_zero`, `Out_of_order_sample_is_dropped`.
- [ ] **D-16 written up**: the "no answer, not zero" rule, and the limits of the different-services
      flag.

---

## 6. Deciding which moves matter, and saving them as `PriceEvent` · 1 to 2 days · BR-02 · needed by 8

- [ ] `PriceEvent`, built on the shared base with created and updated times (`AuditableEntity`), with
      these columns: `Symbol`, `DetectedAt`, `WindowCode`, `WindowStartedAt` and `WindowEndedAt`,
      `Direction`, `StartMid`, `EndMid`, `DeltaAbsolute`, `DeltaPercent`, `VelocityPercentPerMinute`,
      `Volatility` (can be empty), `SampleCount`, `SourceCode`, `BaselineSourceCode`, `IsCrossSource`,
      `ThresholdProfile`, `TriggeredRule`. Prices stored to 4 decimal places, ratios to 6. **No
      `ExplanationId`**: Phase 2 owns that side.
- [ ] `ThresholdProfile` is there because business rule BR-02 makes the thresholds a setting. So what
      an event means depends on settings that may have changed since. Adding the column now is free.
      Adding it later means going back over existing data.
- [ ] `SignificanceOptions.Windows` is a set of thresholds, one per window. A 0.25% move in 5 minutes
      and a 0.25% move in a day aren't the same kind of event. BR-02's 0.25% in 5 minutes is the
      starting default.
- [ ] Windows flagged as crossing services need `CrossSourceMagnitudeMultiplier` times more movement
      (default 2.0) to count.
- [ ] **`price_events` is an ordinary table, not a TimescaleDB one**, and the database change says why
      in a comment.
      - `price_ticks` had to become a TimescaleDB table in the very first change, because converting a
        table that's already full means moving all its data.
      - `price_events` gets a few dozen rows a day.
      - Most importantly, the prices table **deletes everything after 30 days**, and events must be kept
        longer than that. Making this a TimescaleDB table too invites someone to add the same deletion
        rule "for consistency", quietly wiping the product's history.
- [ ] Stop the same event being saved twice, at two levels:
      - In memory: remember when each (symbol, window) last produced an event (`LastEmittedAt`), with a
        waiting period you can set.
      - In the database: **a "must be unique" rule on `(Symbol, WindowCode, WindowEndedAt)`**, as the
        safety net after a restart. Save with "insert, or do nothing if it's already there"
        (`ON CONFLICT DO NOTHING`), instead of letting the save throw an error.
- [ ] Windows inside each other (a 1-day move contains the 1-hour move, which contains the 5-minute
      one) each produce their own event in this batch. Merging them into one event belongs in Phase 2,
      with the explanation feature, which is what actually suffers from three explanations for one move.
      Note that in the decision, not only here.
- [x] ~~Database change adds the two new `price_sources` rows~~: done early, under item 2. It couldn't
      wait for this item, because the "must match a row" rule fails on the first switch to a backup,
      and item 3 delivers that.
- [ ] Tests: `Restart_does_not_re_emit_the_same_event`, `Cross_source_delta_requires_higher_magnitude`.
- [ ] **D-17 written up**: why `price_events` isn't a TimescaleDB table.

---

## 7. Live updates to connected apps (SignalR `PriceHub`) · half a day · FR-5.3

*Needs replanning before it starts: the setup approach below was superseded by D-5. See the note at the
top.*

- [ ] `IPriceBroadcaster` (the thing that sends prices to connected apps) is declared **in** the
      pricing code, and `AddPricingModule` sets up a do-nothing version (`NullPriceBroadcaster`) only if
      nothing else is set up yet (`TryAddSingleton`). `AddRealtimeModule()` then replaces it with the
      real one. The pricing code never refers to SignalR directly, and pricing tests don't need SignalR
      at all.
- [ ] The "do-nothing version first, then replace it" order is the fragile part. **Pin it with a
      test** that builds the full app setup and checks it got the real `SignalRPriceBroadcaster`.
- [ ] A typed SignalR hub (`Hub<IPriceClient>`) with `Subscribe` and `Unsubscribe(symbol)`, over one
      group of listeners per symbol, named `price:{symbol}`.
- [ ] **As soon as an app subscribes, send it the saved price and the latest moves.** Otherwise an app
      connecting three minutes into an eight-hour gap sees nothing for 7 hours 57 minutes, and "connected"
      looks exactly like "the feed is dead".
- [ ] The data sent is its own set of records in `Hubs/`, never the database classes. `Deltas` holds
      **values that can be empty**, so item 5's "no answer, not zero" rule survives the trip to the app.
- [ ] If sending fails, log it and carry on. A price that reached the database is a success even if no
      app heard about it.

---

## 8. Endpoints + a second setup method · 1 day

*Needs replanning before it starts: the first bullet is about a setup approach D-5 replaced. See the
note at the top.*

- [ ] `MapPricingModule(this IEndpointRouteBuilder)`. This contradicted CLAUDE.md's rule of "a single
      `Add<Module>Module` method as each area's only setup method". **Write it up as a decision and
      update CLAUDE.md**, instead of letting the two drift apart.
- [ ] **`GET /v1/price/live`**: serves the saved price **only**, and never fetches a new one. A public
      page that fetched from a service on every visit would let anyone use up a 100-request month by
      reloading it. When there's no saved price yet, answer "unavailable" (HTTP 503) with a message
      explaining why.
- [ ] **`GET /v1/price/history`**: pages through results using the last `(ObservedAt, Id)` seen, not a
      page number, with a cap on how many rows come back. **When `from` is older than the 30 days the
      database keeps, answer "bad request" (HTTP 400) and say that's the reason.** Quietly returning a
      shorter range is the same misleading-data mistake in a different form.
- [ ] **`GET /v1/events`**: newest first, paged the same way, and can be filtered by window and by size
      of move.
- [ ] **`GET /v1/price/sources`**: for each service, its priority, whether it's switched on, its last
      success and failure, its breaker state, and its requests used, limit and reset time. This is how
      to show the fallback goal being met, and how whoever runs the app answers "why is the price eight
      hours old?" without opening the database.
- [ ] **D-18 written up**: the second setup method, and why endpoints don't fit the `Add*Module` one.

---

## 9. Hook up deployment · half a day

- [ ] `docker-compose.yml` and `.env.example` include the new per-service settings.
- [ ] Update CLAUDE.md with: the setup change from item 8, the fallback list in the request counter
      section, and the "no answer, not zero" rule from item 5.
- [ ] Run `dotnet format`.

---

## 10. Test tools · stands alone, do it alongside 2 and 3

Existing pieces to reuse: `Infrastructure/PostgresFixture.cs`, `Infrastructure/ListLogger.cs`,
`Infrastructure/TestPriceSources.cs`, and the fake web handler (`StubHandler`) approach in
`QuotaHandlerTests.cs`.

- [ ] `StubPriceSource`: returns a scripted price or error, and counts how often it's called.
- [ ] `RecordingBroadcaster` (remembers what would have been sent to apps) and `TickReplay` (feeds in a
      list of prices).
- [ ] `AurumApiFactory`, a test version of the whole API (`WebApplicationFactory<Program>`). It needs
      `public partial class Program { }` added at the end of `Program.cs`; letting tests see internal
      code (`InternalsVisibleTo`) isn't enough. **The test API must swap the price timer and the real
      services for fakes.** A timer running inside a test would make tests fail at random, and would
      send real requests against a 100-request month.
- [ ] `DeltaEngineReplayTests`: feed a CSV file of prices through the price-move feature and the
      classifier with a fake clock, and check the exact set of events that comes out. **Write the test
      data by hand, don't record real data.** Free minute-by-minute gold prices aren't available, and a
      real day's prices come with no labels saying what should happen. The test would just lock in
      whatever the code did on day one, which looks thorough and checks nothing. Write it by hand, with
      a note on each stretch saying what it's for:
      - a 0.4% jump in 5 minutes that must produce an event
      - a slow 1% drift that must **not** trigger the 5-minute window
      - a gap where every short window must give no answer
      - a switch between services partway through, with a $3 difference, that must **not** produce an
        event

      Generate the actual numbers from a fixed starting value, reusing the approach in
      `app/src/fixture/rng.ts`, so they come out the same every time.

---

## Done when

- [ ] Real prices arriving from the live API on a schedule (0)
- [ ] Three services set up, each with its own row in the request counter (2)
- [ ] Switch off the primary, and `/v1/price/live` keeps serving with `isFallback: true`, with no gap
      an app would notice (3, 4, 8). How to check it:
      - Use `PriceSources__ApiNinjas__Enabled=false`, not unplugging the network.
      - `api-ninjas` has `Priority: 1`. Switching off a backup changes nothing about which service is
        used, and passes this check without testing the fallback list at all.
      - Switching off the primary makes `goldapi.io`, with its 100 requests a month, the primary. So in
        the same command, raise `PricePolling__PollInterval` above the startup check's minimum, or the
        app refuses to start before the fallback is ever reached.
- [ ] The hand-written test data produces exactly the expected set of `PriceEvent`s (5, 6, 10)
- [ ] An app that subscribes partway through a gap immediately receives the saved price and moves (7)
- [ ] `curl 'localhost:8080/v1/price/history?from=2020-01-01'` answers 400 and names the 30-day limit (8)
- [ ] `docker compose down -v && docker compose up --build` from empty still works (9)
- [ ] D-16 to D-18 written up with the options turned down (each item). D-9 to D-15 are done.

**Deliberately not in this batch:** the dashboard app and its under-1.5-seconds goal, logins, limiting
how often people can call the API, paid tiers, economic and news data, and the paid GoldAPI plan. The
paid plan is still a condition of going live, not of building.
