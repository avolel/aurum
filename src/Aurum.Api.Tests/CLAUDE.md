# Tests

Tests run against a **real Postgres** via Testcontainers, not an in-memory or SQLite provider —
hypertables and vector columns behave differently enough that a mock would validate nothing. One
container per collection (`PostgresCollection`), migrated once; tests share the database and clean up
their own rows in `InitializeAsync`.

`InternalsVisibleTo` is set on `Infrastructure.Data`, `Infrastructure.Pricing` and `Aurum.Api` so
tests reach internal seams — `PostgresQuotaGovernor.ResolvePeriod`, `SourceCircuit`,
`SourceCircuitStore`, `FailoverPriceFeed`, `Program.AddPriceSource<T>` — without widening any public
surface.

Several tests pin a decision rather than find a bug — `Transport_failure_still_spends_the_lease` is
the executable form of "no refunds". Deleting one because it "tests nothing" removes the guard.

Concurrency tests give each caller its own `DbContext` — it is not thread-safe, and the concurrency
under test is between database transactions. A concurrency test that passes on the first try should
be checked for whether it is actually racing before it is believed.

## What every feature's tests must cover

Framework: xUnit + Moq. **Every new CQRS feature needs controller, handler and validator test
files.** Naming: `Controllers/{ControllerName}Tests.cs`,
`Handlers/{Feature}{Command|Query}HandlerTests.cs`, `Validators/{Feature}CommandValidatorTests.cs`.
Methods: `{MethodName}_{Scenario}_{ExpectedResult}` (e.g. `Handle_WithValidId_ReturnsDto`).

- **Controllers:** mock `IMediator` and `IAppLogService<TController>`, never services. Set
  `ControllerContext` with `HttpContext` and claims for auth tests. Arrange/Act/Assert.

  | Endpoint | Required tests |
  |---|---|
  | GET by ID | 200 valid ID, 404 invalid ID, 500 on exception |
  | GET list | 200 with data, 200 empty list, 500 on exception |
  | POST | 200/201 valid input, 400 invalid input, 500 on exception |
  | PUT | 200 valid update, 404 not found, 500 on exception |
  | DELETE | 200 valid ID, 404 not found, 500 on exception |

  Moq: `_mockMediator.Setup(m => m.Send(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))` with
  `.ReturnsAsync(value)`, `.ReturnsAsync((T?)null)` or `.ThrowsAsync(new Exception(...))`;
  `_mockMediator.Verify(..., Times.Once)`.
- **Command handlers:** mock `IUnitOfWork` and the feature's repository. Test success (entity saved,
  correct return), not found (false/null), and that `SaveChangesAsync` is called.
- **Query handlers:** ones that read only memory mock what they read
  (`GetLivePriceQueryHandlerTests`); ones that read the database use the Postgres collection
  (`GetPriceHistoryQueryHandlerTests`). Test data returned, empty result, null for missing ID.
- **Validators:** instantiate directly (`new CreateXCommandValidator()`), call `ValidateAsync`, assert
  `result.IsValid` and `result.Errors`. Required: valid input passes, each required field empty
  fails, max length exact and max length + 1.

## Ticks and the 30-day retention

**Tests that store ticks must date them within 30 days of the real clock.** TimescaleDB's retention
job drops old chunks by the server's real time, not the fake clock, so a fixed old date loses rows
whenever the job runs mid-test and the test fails only sometimes. Every class that stores ticks dates
them from `PostgresFixture.RecentMinute`, real time cut to the whole minute.

## Per-class notes

- **`ResilienceWiringTests`** calls **the production registration**, `Program.AddPriceSource<T>`, and
  swaps only the primary handler for a counting stub. Hand-composing the chain would re-declare the
  order under test. It uses `TimeProvider.System`: `AddResilienceHandler` hands the container's
  `TimeProvider` to Polly, so a `FakeTimeProvider` makes backoff wait on a clock nothing advances and
  the test hangs rather than failing.
- **The backoff check is split in two** so a failure says which side moved.
  `PriceSourcesOptionsTests.MinimumTotalTimeout_sums_an_exponential_schedule` tests the formula with
  hand-worked expectations and no clock; `Backoff_schedule_matches_what_MinimumTotalTimeout_models`
  measures Polly at three base/attempt rows (~1.6s of real waiting). Keep expectations typed by hand
  — a `base × 2^i` loop re-implements the formula. Every other wiring test overrides
  `RetryBackoffBase` to 1ms, so `Resilience_defaults_are_the_shipped_attempt_count_and_backoff` is
  what notices the production default changing.
- **`SourceCircuitTests`, `FailoverPriceFeedTests`, `LatestQuoteCacheTests`** need **no container**
  and run in milliseconds — the payoff of measuring time by comparing two `TimeProvider` reads
  instead of holding a timer or eviction, and half of D-14's case against Polly's breaker.
- **`LatestQuoteCacheTests.Concurrent_records_leave_a_consistent_entry`** runs rounds on dedicated
  threads behind a `Barrier` and checks after every round. Its first version checked only the final
  state and **passed every run against a deliberately broken check-then-set implementation**. Do not
  collapse it into one long run, and do not swap the threads for `Task.Run`: pool threads arrive at
  the barrier staggered.
- **`LatestQuoteCacheWarmupTests`** inserts ticks with **seeded** source codes
  (`price_ticks.SourceCode` has a foreign key to `price_sources`) and deletes *every* tick in
  `InitializeAsync`, because warm-up reads the newest row per symbol whatever its source.
- **`PricePollingServiceTests`** needs Postgres because the `price_sources` projection is half its
  subject. It deletes every tick (a newer leftover row would be warmed first and the cache would drop
  the test's quote as older). `A_poll_writes_the_cache_before_it_persists` makes the save fail with an
  unseeded source code so the foreign key rejects it — no mocked `DbContext`. `WaitForCachedAsync` is
  `WaitUntilAsync` for the cache. **The waits are the fragile part:**
  `ScriptedPriceFeed.WaitForCallAsync` completes when the feed is *entered*, and the poller writes the
  tick afterwards, so asserting straight after it races the save. `WaitUntilAsync` polls the database
  for the side effect instead. Both this class and the warm-up class rely on the collection running
  one class at a time.
- **`PricePollingServiceTests` and `PriceEventSqlTests`** delete every `price_events` row in
  `InitializeAsync`; the waiting-period check reads across sources, so a leftover row suppresses the
  test's insert.
- **`PriceHubTests`** needs **no container**: an in-test `WebApplication` on `TestServer` with
  `AddSignalR`, `MapHub<PriceHub>`, the real cache and `DeltaEngine` on a `FakeTimeProvider`, and a
  real `HubConnection`. Transport is **long polling** via `server.CreateHandler()` — `TestServer` has
  no socket — so "nothing arrived" assertions wait 250ms. The fake clock is passed by constructor and
  **not registered in the container**, so SignalR's own timers stay on real time. Messages are
  received as `JsonElement` so assertions check the wire (camelCase, nulls inside the dictionary). It
  does not use `Program.cs`.
- **`AurumApiFactory`** (`WebApplicationFactory<Program>`) boots the real `Program.cs` against the
  collection's Postgres. It **removes `PricePollingService` and replaces every `IPriceSource` with a
  faulting `ScriptedPriceSource`**: a poller in a test fires on its own timer and spends real
  requests. So nothing fills the cache; a test that needs a held price calls
  `ILatestQuoteCache.Record` itself. Settings go in with `UseSetting`, not
  `ConfigureAppConfiguration` — `Program.cs` reads the connection string before `Build()`, and only
  host settings are visible that early. API keys are set per config key (`GoldApiIo`, …) and
  `Macro:Fred:ApiKey` and `Macro:Bls:ApiKey` are set, because blank keys refuse to boot (D-9).
- **`BlsClientTests`** needs **no container**: `BlsClient` over a `CountingHandler` stub that records
  the `POST` body. `Modules/Macro/bls-2026.json` is a real BLS answer for all four series from
  2026-11-11. The refusal body is BLS's real one, key included, so the scrub test is honest.
- **`FredClientTests`** needs **no container**: the real `QueryKeyAuthHandler` over a
  `CountingHandler` stub. `Modules/Macro/*.json` are real FRED answers from 2026-11-07, copied to the
  test output by the csproj. CPI and 10-year contain real `"."` rows (October 2025 CPI, two market
  holidays); the payrolls file has October 2025 published late, with November. Re-save them with a
  real call rather than editing by hand, and check a new file for the key before committing it.
- **`DeltaEngineReplayTests`** replays `Modules/Pricing/DeltaEngineReplay.csv` through `DeltaEngine`
  and `SignificanceClassifier` on a fake clock and asserts the exact event list (D-21). Rows are
  **stretches** (`fromMinute,toMinute,fromMid,toMid,source`); `TickReplay` expands them to one price
  a minute plus seeded mulberry32 noise (a port of `app/src/fixture/rng.ts`). Each stretch's `#` note
  says what it is for and which thresholds it clears, by how much — keep every margin above the noise
  ($0.20 a price). The test owns its thresholds rather than reading `appsettings.json`. The expected
  list has repeats: the classifier has no waiting period; that lives in the insert (D-18).
- **`QuotaHandlerTests`** drives the handler through a real `HttpClient` over a stub inner handler,
  because `HttpClient` does its own exception handling on the way out — bypassing it would not prove
  `QuotaExhaustedException` reaches `PricePollingService`'s catch. `Infrastructure/ListLogger.cs`
  captures log entries where the log line *is* the deliverable (so far only the governor's denial
  message).
