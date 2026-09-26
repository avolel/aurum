# Phase 1 — backend tranche

Everything below is yours. The foundation is in place and green; see `ops/decisions/decisions.md`
for what's already decided and what is still unverified.

Ordered so each item is verifiable when you finish it, following the sequence in the Phase 1 plan.
Items 2–9 are a hard chain — each sits on the one above it — so they cannot be reordered or done in
parallel. Items 0 and 10 are independent and fit in any gap.

The goal: `docker compose up` produces an API serving a live price from a three-source failover
chain, flagging significant moves, over SignalR and REST. **This tranche is backend only** — the
Expo/RNW dashboard, and its FCP < 1.5s criterion, are a second tranche planned once the API surface
is settled.

Decisions get recorded in `ops/decisions/decisions.md` **as each item lands**, not in a sweep at the
end. A decision written afterwards is a decision reconstructed, and the rejected alternatives — the
part worth keeping — are exactly what gets lost.

> **Decision numbering.** `D-9` … `D-14` are taken and recorded: per-source configuration lookup
> and nested options validation (item 1), poll cadence placement (item 2), three free sources
> (item 2), `IEnumerable<IPriceSource>` over keyed DI (item 2), the per-attempt timeout's placement
> in the resilience pipeline (item 3), and the hand-rolled circuit breaker (item 3). The Phase 1
> plan's provisional list has now shifted by **four**; the numbers used below are the real ones.
> Item 2 briefly carried two bullets both labelled `D-10` — the cadence decision took that number,
> and the other two became `D-11` and `D-12`, pushing items 3–8 down by two. The timeout decision
> then took `D-13`, which item 3's breaker bullet had been holding, pushing items 5–8 down by one
> more: the delta engine's null-window invariant is `D-15`, not `D-13` as `D-11`'s prose said.

---

## 0. Carried over from the foundation: real ticks on a schedule · 1 hr · exit criterion

Still unticked from the previous todo, and still the only thing standing between "the code is right"
and "the code works against the actual provider". Independent of everything below — do it as soon as
there is a real key.

- [x] Real API key in `.env`, `docker compose up`.
- [x] `SELECT * FROM price_ticks ORDER BY "ObservedAt" DESC LIMIT 10;` — real prices, sane
      `ObservedAt`/`ReceivedAt` gap, `Symbol = 'XAUUSD'`, `SourceCode = 'goldapi.io'`.
- [x] `SELECT * FROM price_sources;` — `LastSuccessAt` advancing.
- [x] Sanity-check the mid against a public spot quote. `PriceQuote.Normalize` prefers GoldAPI's
      `price` field over `(bid+ask)/2`; confirm that's the number you actually want on the chart.
      Verified 2026-09-07: `Mid` differs from `(bid+ask)/2` by at most $0.16 on a ~$4,400 price
      (0.004%), so GoldAPI's `price` *is* a midpoint and the preference is a no-op for this source.
      The comparison did surface the hazard it was written for, on a different axis: the public
      reference quotes its **ask** as the headline price and carries a ~$14.50 spread against
      GoldAPI's ~$0.83. `Mid` is comparable across the two; `Bid`/`Ask` are not. Item 2 puts three
      sources into the same columns — decide there whether bid/ask are cross-source comparable at
      all, or only ever read per-source.

Note that `appsettings.json` now ships `ApiKey` as the empty string on purpose, so a missing key
fails the process at boot rather than spending the month one 401 at a time. That is the D-9
behaviour working, not a misconfiguration.

---

## 1. Options reshape + validation fix + governor lookup · DONE

`PriceSourcesOptions` is a map of sources looked up by `SourceCode`; `PriceSourcesOptionsValidator`
performs the nested-annotation descent that `ValidateDataAnnotations()` does not.

- [x] **`GetCurrentPeriod` is a total lookup.** The `switch` with its fall-through to a hardcoded
      100-request calendar-month budget is gone; an unconfigured source throws.
      `An_unconfigured_source_throws_rather_than_defaulting`.
- [x] **Nested annotations actually fire.** `ApiKey`'s `[Required]` and `MonthlyRequestLimit`'s
      `[Range]` had never executed. `A_missing_api_key_fails_the_host_at_boot`.
- [x] **Cross-field checks moved to boot** — the cadence-vs-budget guard out of
      `PricePollingService.GuardPollBudget`, plus `RollingThirtyDays` requiring a `PeriodAnchor`.
- [x] **D-9 recorded**, with the rejected alternatives (keep the switch and add an arm; key the map
      by source code; the options source generator).

~~Still open from this item~~ — **closed in item 2.** `RequireByCode` threw from
`GoldApiIoSource`'s and `PricePollingService`'s field initialisers, i.e. during DI construction
rather than at `ValidateOnStart`. `AddPriceSource<T>` now emits a `RegisteredPriceSource` tag per
source and `PriceSourcesOptionsValidator` compares those to the configured codes, so a source
registered in code without a configuration entry fails the host at boot with a named error. The
`RequireByCode` throw is a genuine backstop now rather than the primary signal.

---

## 2. Three sources + registration helper · DONE · blocks 3

Three free sources so the aggregate cadence is usable at $0 and the failover chain has something real
to fail over to. Each keeps its own quota accounting row.

- [x] `ApiNinjasSource` — `/v1/goldprice` returns `{name, price, updated}`. Mid only;
      `PriceQuote.Normalize` already handles that. **Gold-only: reject a non-`XAUUSD` symbol
      explicitly** rather than returning gold for whatever was asked.
- [x] `MetalpriceApiSource` — two traps, both silent:
      - It authenticates by **query parameter**, so the request URI must never reach a
        `PriceSourceException` message or a retry log.
      - `/v1/latest?base=USD&currencies=XAU` returns *ounces per USD* (~0.00042), and `Mid` is
        `decimal(18,4)`, so that rounds to `0.0004` with no exception and the delta engine sees a
        $2,300 crash. Request `base=XAU&currencies=USD` **and** sanity-band the mid before returning.
- [x] `AddPriceSource<T>` private helper in `PricingModule`, so the handler ordering from item 3
      cannot be got wrong per source.
- [x] Sources resolve as `IEnumerable<IPriceSource>` ordered by `Priority`.
- [x] Cadence guard: **the primary carries the worst case, backups are exempt.** One feed-wide
      `PricePolling:PollInterval`; the enabled entry with the lowest `Priority` must fund it for a
      full period. Holding every source to "this source serves every poll" reads as the safe rule
      but pegs the cadence to the smallest budget in the file — GoldAPI's 100 a month would cap the
      whole feed at one poll every 7h26m however much headroom the others have. A backup exhausting
      mid-outage is counted and clamped by the governor; `PricePollingService` logs each backup's
      coverage in days at startup so the exemption is visible. Ties for the lowest `Priority`, and a
      configuration with no enabled source, are refused. **D-10 recorded.**
- [x] Validator asserts every registered source code has a configuration entry (carried from item 1).
      `AddPriceSource<T>` emits a `RegisteredPriceSource` tag; the validator compares those against
      the configured codes, so the throw is a `ValidateOnStart` failure rather than a DI-construction
      one.
- [x] `appsettings.json`, `.env.example` and `docker-compose.yml` carry the two new sources.
      Note that compose exposes `MonthlyRequestLimit` as an override for GoldAPI only — the one
      limit expected to change, on the paid tier (D-8). The other two take their limits from
      `appsettings.json`.
- [x] **Seed migration for the two new `price_sources` rows.** `price_ticks.SourceCode` is a foreign
      key, so the first failover to an unregistered source would have thrown `23503` from
      `SaveChangesAsync` *after* the request was sent and the lease spent.
      `20260919102443_SeedAdditionalPriceSources`, using `InitialSchema`'s
      `INSERT … ON CONFLICT DO NOTHING` idiom. This was previously written down only under item 6.
- [x] **D-11 recorded** — three free sources rather than the paid tier, and what it still does not
      buy: API Ninjas is the only one with real headroom and it is gold-only, so it cannot serve
      Phase 6's multi-metal work.
- [x] **D-12 recorded** — sources as `IEnumerable<IPriceSource>` rather than keyed DI. Keyed DI would
      force the chain to carry its own list of key strings, duplicating what `Priority` expresses.

---

## 3. Failover chain + circuit breaker · 1–2 days · FR-1.3 · blocks 4

`IPriceFeed` is a **separate interface**, not a composite `IPriceSource` — a composite would land in
the same `IEnumerable<IPriceSource>` it consumes, and the result has to carry `AttemptedSources` /
`UsedFallback`, which the single-provider contract should not grow.

- [x] Add `Microsoft.Extensions.Http.Resilience`. Register the Polly pipeline **before**
      `.AddHttpMessageHandler(quota)`, giving `resilience → QuotaHandler → primary`. This is what
      makes "the handler sees exactly what leaves the process" mechanical rather than conventional.
- [x] Retry `ShouldHandle` **excludes** `QuotaExhaustedException` — the source is healthy and broke,
      so retrying it spends budget that was already denied.
- [x] Exception semantics, each one distinct:
      - `QuotaExhaustedException` → never retried, **not** a circuit fault. Move on immediately.
      - `HttpRequestException` / timeout / failing status → retried by the pipeline, which is the
        only layer that observes them, and on final failure counts as a circuit fault.
      - `PriceSourceException` → **not** in `ShouldHandle`; the predicate cannot observe it. Every
        source parses the body above the handler chain, so it is raised after `SendAsync` returned
        an outcome the pipeline already judged successful and unwound — by then the retries for the
        underlying status or transport failure are spent. Failover and circuit fault only, counted
        by `FailoverPriceFeed`, which sits above the sources and does see it. Writing it into the
        predicate buys nothing and reads as coverage the pipeline does not have.
      - Every source down → `AllSourcesFailedException` carrying per-source outcomes.
- [x] The poller sleeps **only when every source is quota-exhausted**, and then until the earliest
      `ResetsAt`. Today's single-source `Task.Delay` would idle the whole poller for a month.
- [x] Circuit breaker hand-rolled in `FailoverPriceFeed`, not Polly's.
- [x] Circuit state is in-memory and authoritative; `PriceSource.LastFailureAt/Reason` is its
      projection, written once per poll. **Do not rehydrate it at startup** — a fresh process should
      re-probe every source. Say so in the class remarks, or it reads as an oversight against the
      quota governor's durability rule sitting right next to it.
- [x] `Each_retry_attempt_spends_its_own_lease` — stub returns 500 three times under a 3-attempt
      retry; assert `RequestsUsed == 3`. This is the executable form of "QuotaHandler sits below
      retries", currently enforced only by the order of two lines in `PricingModule.cs`.
- [x] `Quota_exhaustion_is_not_retried` — split into `A_provider_rejection_is_not_retried` (429:
      exactly one call, `ProviderRejectedAt` set) and `A_denied_lease_never_reaches_the_network`
      (window seeded at its limit: zero calls, counter unchanged — a denial must not burn budget
      proving it is a denial).
- [x] `Open_circuit_source_is_not_called` — asserts `CallCount == 0`, not merely the outcome.
- [x] **D-14 recorded** — why the breaker is hand-rolled: `GoldApiIoSource` throws
      `PriceSourceException` *after* a 200 when the body is garbage, so a pipeline breaker would
      never trip on a degrading provider while the chain spent a lease per poll; and a Polly
      breaker's break duration runs on wall clock, which breaks the injected-`TimeProvider`
      invariant and makes the state untestable on `FakeTimeProvider`.

**Landed 2026-09-26.** `FailoverPriceFeed`, `SourceCircuit`, `SourceCircuitStore`, the
`PricePollingService` rewrite and 25 new tests. Two things found on the way that were not in the
plan:

- **The retry predicate never fired on a failing status code.** It was
  `PredicateBuilder.Handle<TimeoutRejectedException>().Handle<HttpRequestException>()`, both of
  which are *exception* clauses — but `HttpClient.GetAsync` returns a 500, it does not throw one.
  A provider answering 503 all day was never retried at all, and the strategy read as configured
  while doing nothing on the most common failure it exists for. Fixed with a `.HandleResult(...)`
  clause for `RequestTimeout` and `>= InternalServerError`. Caught by
  `Each_retry_attempt_spends_its_own_lease` on its first run, which is the whole argument for that
  test.
- **`AddResilienceHandler` resolves `TimeProvider` from the container** and hands it to Polly's
  strategies. Registering a `FakeTimeProvider` makes the retry backoff wait on a clock nothing
  advances, so the test hangs rather than failing. Noted in `ResilienceWiringTests`' remarks.

Not done, and deliberately: the plan's `PriceFeed:Resilience:MaxAttempts` knob and the cadence
guard's multiplier. `MaxRetryAttempts` is still the hardcoded 2 that D-13 shipped, so the budget
guard under-counts worst-case spend by 3x. **Allocated to item 4** — it is a configuration and
validator change with its own tests, and folding it in here would have put an unreviewed spend
multiplier in the same commit as the chain.

---

## 4. Latest-quote cache · half a day · blocks 8

- [ ] Singleton `ConcurrentDictionary<symbol, LatestQuote>` over an immutable record, so replacement
      is one atomic reference swap.
- [ ] **Not `IMemoryCache`.** Eviction is exactly wrong when the cadence can be hours, and freshness
      has to be *computed at read* against the injected clock rather than enforced by expiry.
- [ ] Holds quote + `IsFallback` + `AttemptedSources`; exposes `Age` / `IsStale` against a
      configurable threshold defaulting to 2× `PollInterval` — which is what makes §8's criterion
      observable instead of aspirational.
- [ ] **Rehydrate at startup** from the newest tick per symbol, sharing item 6's warmup read.
      Otherwise a restart leaves `/v1/price/live` empty for up to a full poll interval.
- [ ] Poller write order is **cache → persist → broadcast**. A database hiccup must not hide a price
      we successfully fetched; the cache is not the system of record.

---

## 5. Delta engine · 2–3 days · the CORE piece · FR-1.4 · blocks 6

**The invariant, verbatim in the class remarks:**

> A window delta is either computed from two real observed samples whose spacing brackets the window
> within tolerance, or it is **null**. The engine never fabricates a baseline — no zero-fill, no
> interpolation, no carry-forward. A null window means "we do not know", and every consumer must
> render it as unknown rather than as zero.

With hours between ticks the 1m/5m/15m windows hold one sample, and `latest - oldest-in-window`
returns 0.00% — which is not "no movement", it is "no data". Conflating them is §14's
"delayed data misleading users" in its purest form.

- [ ] **One ring buffer per symbol**, sized to the longest window (1D), serving all six windows by
      binary search. Not one buffer per window: that stores each sample six times, keeps six eviction
      boundaries consistent, and hands you the *oldest sample still in the window*, which is the
      wrong baseline.
- [ ] `readonly struct Sample { DateTimeOffset ObservedAt; decimal Mid; byte SourceOrdinal; }` in a
      `Sample[]` with head/count. Capacity bounded by config (`MaxSamplesPerSymbol`, default 8,640 —
      24h at 10s, ~200 KB/symbol). Bounded by construction, not by hope.
- [ ] **Baseline is the predecessor**, not the successor: binary search for the last sample at or
      before `t = now - W`. A successor silently *shrinks* the window, so an 8-hour-spaced feed would
      compute a "1 hour delta" over 8 hours and label it 1h — mislabelling rather than declining.
- [ ] Null when the gap `t - baseline.ObservedAt` exceeds `Tolerance(W)` (default `W/2`), when fewer
      than two samples exist, or when all data postdates `t`.
- [ ] **Out-of-order samples: drop, never reorder.** Monotonicity *is* what makes the binary search
      valid. Drop any sample with `ObservedAt <= last`, log at Debug, and **increment an observable
      counter** — "the price stopped moving" and "we are dropping every tick" look identical from
      outside. Reordering would produce a series that is sorted but not real.
- [ ] **Cross-source level offsets — the most likely source of false events.** Providers differ on
      the level of gold by a few dollars, so a failover between polls produces an apparent move of
      exactly that offset, which at 0.25%-in-5m fabricates an event Phase 2 will then explain
      confidently. `Sample` carries its source ordinal; a window whose baseline and latest differ in
      source is flagged `CrossSource`. **Record in D-15 that this is a mitigation, not a fix** — the
      fix is per-source calibration offsets, which needs data we do not have.
- [ ] **Volatility on three samples is meaningless.** Require `MinSamplesForVolatility` (default 5)
      or report `Volatility = null`, so the classifier's volatility rule is *inapplicable* rather
      than degenerate.
- [ ] Rehydration reads `WHERE Symbol = @s AND ObservedAt >= now - LongestWindow ORDER BY ObservedAt
      DESC LIMIT @capacity`, reversed in memory — descending matches the existing
      `(Symbol ASC, ObservedAt DESC)` index.
- [ ] Warmup is **`EnsureWarmAsync(ct)`, idempotent**, awaited by the poller before its first poll
      and by the endpoints' first read. Not a separate `IHostedService`: "hosted services start in
      registration order" is an invariant nobody re-reads before adding a service, and a violation
      surfaces as a `null` first delta that looks like a normal cold start.
- [ ] `DeltaEngine` is a singleton taking `IServiceScopeFactory` for the warmup query — same
      reasoning as `QuotaHandler`, a long-lived object must not capture a scoped `DbContext`.
- [ ] `Window_with_no_bracketing_sample_is_null_not_zero`, `Out_of_order_sample_is_dropped`.
- [ ] **D-15 recorded** — the null-window invariant, and the cross-source mitigation's limits.

---

## 6. Significance classifier + `PriceEvent` · 1–2 days · BR-02 · blocks 8

- [ ] `PriceEvent : AuditableEntity` — `Symbol`, `DetectedAt`, `WindowCode`,
      `WindowStartedAt/EndedAt`, `Direction`, `StartMid`, `EndMid`, `DeltaAbsolute`, `DeltaPercent`,
      `VelocityPercentPerMinute`, `Volatility` (nullable), `SampleCount`, `SourceCode`,
      `BaselineSourceCode`, `IsCrossSource`, `ThresholdProfile`, `TriggeredRule`. Prices `(18,4)`,
      ratios `(18,6)`. **No `ExplanationId`** — Phase 2 owns that side.
- [ ] `ThresholdProfile` because BR-02 makes thresholds configurable, so an event's meaning depends
      on config that may have changed since. The column is free now and a data migration later.
- [ ] `SignificanceOptions.Windows` is a dictionary of per-window thresholds — 0.25% in 5m and 0.25%
      in 1D are not the same event. BR-02's 0.25%/5m is the anchor default.
- [ ] Cross-source windows demand a `CrossSourceMagnitudeMultiplier` (default 2.0) more movement.
- [ ] **`price_events` is a plain table, not a hypertable**, and the migration says why in a comment.
      `price_ticks` had to be one in the first migration because `create_hypertable` on a populated
      table is a data migration; `price_events` is dozens of rows a day. More decisively, the ticks
      hypertable carries a **30-day retention policy** and events must outlive it, so making this a
      hypertable invites someone to attach the same policy by symmetry and quietly delete the
      product's memory.
- [ ] Dedup in two layers: in-memory `LastEmittedAt` per `(symbol, window)` with a configurable
      cooldown, and a **unique index on `(Symbol, WindowCode, WindowEndedAt)`** as the restart
      backstop — inserted with `ON CONFLICT DO NOTHING` rather than letting `SaveChangesAsync` throw.
- [ ] Nested windows (a 1D move contains the 1h contains the 5m) emit separately this tranche.
      Collapsing them into one episode belongs in Phase 2, with the explanation pipeline that
      actually suffers from three explanations per move — note it in the decision, not just here.
- [x] ~~Migration seeds the two new `price_sources` rows~~ — done early, under item 2. It could not
      wait for this item: the foreign key fires on the first failover, which item 3 delivers.
- [ ] `Restart_does_not_re_emit_the_same_event`, `Cross_source_delta_requires_higher_magnitude`.
- [ ] **D-16 recorded** — why `price_events` is not a hypertable.

---

## 7. SignalR `PriceHub` · half a day · FR-5.3

- [ ] `IPriceBroadcaster` is declared **in** the Pricing module and registered by `AddPricingModule`
      as `NullPriceBroadcaster` via `TryAddSingleton`; `AddRealtimeModule()` replaces it. The module
      never references `IHubContext`, and Pricing tests need no SignalR runtime.
- [ ] The TryAdd-then-replace ordering is the fragile part — **pin it with a test** that resolves the
      composed container and asserts it got `SignalRPriceBroadcaster`.
- [ ] `Hub<IPriceClient>` with `Subscribe/Unsubscribe(symbol)` over groups `price:{symbol}`.
- [ ] **On subscribe, immediately send the cached quote and delta snapshot.** Otherwise a client
      connecting three minutes into an eight-hour interval sees nothing for 7h57m, and "socket
      connected" is indistinguishable from "feed dead".
- [ ] DTOs are explicit records in `Hubs/`, never EF entities, and `Deltas` carries **nullable**
      window values so item 5's invariant survives the wire.
- [ ] Broadcast failures are swallowed and logged: a tick that reached Postgres is a success even if
      nobody heard it.

---

## 8. Endpoints + the second module seam · 1 day

- [ ] `MapPricingModule(this IEndpointRouteBuilder)`. This contradicts CLAUDE.md's "a single
      `Add<Module>Module` extension method as its only registration seam" — **record it as a decision
      and update CLAUDE.md** rather than letting the two drift.
- [ ] **`GET /v1/price/live`** — serves the cache **only**, never triggers a fetch. A public endpoint
      that polls upstream is a quota denial-of-service against a 100-request month. `503` with an
      explanatory body when cold.
- [ ] **`GET /v1/price/history`** — keyset on `(ObservedAt, Id)`, capped `take`. **`400` when `from`
      predates the 30-day retention horizon, naming retention as the reason.** Silently truncating
      the range is the same misleading-data failure in different clothes.
- [ ] **`GET /v1/events`** — newest first, keyset paged, filterable by window and magnitude.
- [ ] **`GET /v1/price/sources`** — per-source priority, enabled, last success/failure, circuit
      state, quota used/limit/resets. This is how the failover exit criterion gets demonstrated, and
      how an operator answers "why is the price eight hours old" without a psql session.
- [ ] **D-17 recorded** — the second module seam, and why endpoints do not fit the `Add*Module` one.

---

## 9. Wire up the deployment · half a day

- [ ] `docker-compose.yml` and `.env.example` carry the new per-source environment variables.
- [ ] CLAUDE.md updated: the module-seam change from item 8, the failover chain in the quota-chain
      section, and the delta engine's null-window invariant.
- [ ] `dotnet format`.

---

## 10. Test infrastructure · independent, do it alongside 2–3

Existing patterns to reuse: `Infrastructure/PostgresFixture.cs`, `Infrastructure/ListLogger.cs`,
`Infrastructure/TestPriceSources.cs`, and the `StubHandler` idiom in `QuotaHandlerTests.cs`.

- [ ] `StubPriceSource` — scripted quote or exception, records call count.
- [ ] `RecordingBroadcaster`, `TickReplay`.
- [ ] `AurumApiFactory : WebApplicationFactory<Program>`, which needs `public partial class Program
      { }` appended to `Program.cs` — `InternalsVisibleTo` does not substitute. **The factory must
      replace the poller and the real sources with stubs**; a hosted service polling inside a test
      host is both flake and real HTTP calls against a 100-request month.
- [ ] `DeltaEngineReplayTests` — drive an embedded CSV through the engine and classifier on a
      `FakeTimeProvider`, asserting the exact event set. **Author the fixture, do not record one:**
      free minute-resolution XAU/USD history is not available, and an unlabelled real session has no
      ground truth, so the test would encode whatever the implementation did on day one — which looks
      rigorous and pins nothing. Hand-author it with intent annotated per segment:
      - a 0.4%/5m spike that must emit
      - a slow 1% drift that must **not** trip 5m
      - a gap where all short windows must be `null`
      - a mid-session source switch with a $3 offset that must **not** emit

      Generate the numeric path from a fixed seed, reusing the retained `app/src/fixture/rng.ts`
      approach so it is reproducible.

---

## Done when

- [ ] Real ticks landing from the live API on a schedule (0)
- [ ] Three sources configured, each with its own quota row (2)
- [ ] Kill the primary — `/v1/price/live` keeps serving with `isFallback: true` and no
      client-visible gap (3, 4, 8). Verify with `PriceSources__ApiNinjas__Enabled=false`,
      not by unplugging the network. api-ninjas is `Priority: 1`; disabling a backup leaves
      selection untouched and passes the criterion without exercising the chain. Disabling
      the primary re-elects goldapi.io and its 100-request tier, so raise
      `PricePolling__PollInterval` past the budget guard's minimum in the same command or
      the host refuses to boot before the failover path is reached.
- [ ] The replay fixture produces the exact expected `PriceEvent` set (5, 6, 10)
- [ ] A client subscribing mid-interval immediately receives the cached quote and deltas (7)
- [ ] `curl 'localhost:8080/v1/price/history?from=2020-01-01'` returns 400 naming retention (8)
- [ ] `docker compose down -v && docker compose up --build` from clean still works (9)
- [ ] D-15 … D-17 recorded with their rejected alternatives (each item). D-9 … D-14 are done.

**Explicitly not in this tranche:** the Expo/RNW dashboard and its FCP < 1.5s criterion, auth, rate
limiting, tier gating, macro and news ingestion, and the paid GoldAPI upgrade — still a go-live gate,
not a build gate.
