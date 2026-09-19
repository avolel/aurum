# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                              # whole solution (Aurum.slnx)
dotnet test                                               # all tests — needs Docker
dotnet test --filter "FullyQualifiedName~QuotaGovernorTests"   # one class
dotnet test --filter "FullyQualifiedName~ResolvePeriod"        # one test; no container, sub-second
dotnet format                                             # before committing
docker compose up --build                                 # full stack: postgres, ollama, api
```

`~` in `--filter` is a substring match on the fully-qualified name, so it matches namespace, class
or method.

Migrations (run from `src/Aurum.Api`):

```bash
export ConnectionStrings__Aurum="Host=localhost;Database=aurum;Username=aurum;Password=..."
dotnet ef migrations add <Name>
dotnet ef migrations script --idempotent    # inspect before applying
```

`dotnet ef` builds the real host to get its `DbContext`, and `Program.cs` throws without a
connection string. Without one you get a misleading `Unable to resolve service for type
'DbContextOptions<AurumDbContext>'` — EF has already fallen back to activating the context without
the application service provider by then, and the connection-string error is one line above it. The
value need not point at a live database for `migrations add` or `script`.

Tests require Docker. If `docker ps` is permission-denied, `sudo usermod -aG docker $USER` and log
back in — nothing runs without it.

## Architecture

One ASP.NET Core project, `src/Aurum.Api`, with **modules as folders rather than assemblies**
(decision D-5). Each module exposes a single `Add<Module>Module` extension method as its only
registration seam; `Program.cs` calls those and knows nothing else about module internals.

### The quota chain — the part that needs multiple files to understand

GoldAPI's free tier is roughly **100 requests per month**. Everything below exists because an
in-memory counter that resets with the container can spend a month of budget in an afternoon while
every individual request looks fine.

```
PricePollingService  →  HttpClient (typed, IPriceSource)
                            └─ QuotaHandler (DelegatingHandler)
                                  └─ IQuotaGovernor → api_quota_windows
```

Three placement decisions here that look arbitrary in isolation:

- **`QuotaHandler` is a `DelegatingHandler`, not a wrapper around `IPriceSource`.** Phase 1 adds
  Polly retries and a failover chain above `IPriceSource`; a governor at that level would let every
  retry spend quota uncounted. The handler sees exactly what leaves the process.
- **It takes `IServiceScopeFactory`, not `IQuotaGovernor`.** `IHttpClientFactory` pools handlers for
  minutes, so injecting the scoped governor would capture a `DbContext` in a long-lived object
  shared across concurrent requests.
- **`PostgresQuotaGovernor.AcquireAsync` uses raw SQL** — a single
  `INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`. Do not "simplify" this into EF. Loading the
  row, deciding in C#, and saving leaves a gap between the check and the increment that no C# can
  close; `ON CONFLICT` re-reads the row under a lock, which is the entire concurrency guarantee.
  `Concurrent_acquires_never_oversubscribe` is the test that catches a regression here. The class
  remarks and `ops/decisions/decisions.md` (D-7) carry the full reasoning.
- **`ReportProviderRejectionAsync` is an upsert for the same reason**, not the `UPDATE … WHERE` it
  looks like it should be. An update changes zero rows and reports success when the period has no
  row yet, which happens whenever a request straddles a period boundary — the acquire is charged to
  the old period, the rejection arrives in the new one, and the clamp disappears silently.

Related invariants:

- Period rollover needs no scheduled job. A new period means a new `PeriodKey`, so no row exists and
  the next acquire creates one. Spent rows are never reset — they are history.
- `QuotaStatus.Limit - Used` is **not** remaining budget. When `ProviderRejected` is set, remaining
  is zero regardless of the counter. The clamp never lives in the counter: a row reading "0 used of
  100, rejected" is deliberate, and it is the only signal that our accounting has drifted from the
  provider's.
- `AcquireAsync` reads the row back on the **denial path only**, to log whether the cause was a
  spent budget or a provider rejection. That read is outside the atomic statement and is therefore
  diagnostic only — it can be stale by a rejection or a period rollover. Nothing may branch on it.
- The cadence is one feed-wide value, `PricePolling:PollInterval`, and it is coupled to the
  **primary** source's `MonthlyRequestLimit` only — the enabled entry with the lowest `Priority`.
  `PriceSourcesOptionsValidator` fails the process at boot when that budget cannot fund the cadence
  for a period. Backups are deliberately exempt (D-10): holding every source to the full period
  pegs the cadence to the smallest budget in the file, so each added provider could only slow the
  feed. A backup that empties its budget during a long primary outage is counted and clamped by the
  governor, which is the accounting working rather than the failure it guards against; the cost is
  only visible in `PricePollingService`'s startup coverage log. The validator also refuses a tie for
  the lowest `Priority` and a configuration with nothing enabled.
- The injected `TimeProvider` is the sole authority for every timestamp the app writes, not just
  period boundaries; `now()` never appears in governor SQL. `AurumDbContext` takes it as a
  **required** constructor parameter so no construction site can silently fall back to wall clock —
  that fallback is precisely what let `ApplyAuditFields` stamp wall-clock times while the governor
  wrote clock-injected ones, so two rows in the same table carried timestamps from two different
  clocks — invisible in production, a month apart under a fake clock in tests. Note that raw SQL
  bypasses `ApplyAuditFields` entirely, so `CreatedAt` / `UpdatedAt` must still be passed explicitly
  there.

### Database

Postgres with TimescaleDB and pgvector (`timescale/timescaledb-ha:pg17`, decision D-3).

- `price_ticks` has a **composite primary key `(ObservedAt, Id)`**. TimescaleDB rejects any unique
  index that omits the partitioning column, so a surrogate-only key makes `create_hypertable` fail.
- The first migration does more than create tables: hypertable conversion, the 30-day retention
  policy, and the price-source seed all live there. Read it before adding a migration that touches
  `price_ticks`.
- Extensions are declared in **both** `ops/db/init/01-extensions.sql` (superuser, once, on an empty
  volume) and `AurumDbContext.OnModelCreating`. Harmless today only because the compose app role is
  the bootstrap superuser; introducing a least-privilege role means the init script must become the
  sole owner.
- `Program.cs` migrates at startup. Correct for one instance, wrong the moment the API scales out.
  `PricePollingService` carries the same single-instance assumption.

### Configuration

The `PriceSources` section binds to a **map of sources**, not a property per provider
(`PriceSourcesOptions.Sources`, keyed by config key, with the provider's natural key carried inside
as `SourceCode`). Values come from `appsettings.json` and are overridden by environment variables
using `__` as the section separator (`PriceSources__GoldApiIo__MonthlyRequestLimit`), which is how
`docker-compose.yml` and `.env` set them.

- **Look sources up with `TryGetByCode` / `RequireByCode`, never with a `switch` on the code.** A
  switch needs a fall-through arm, and a fall-through arm is a fabricated configuration —
  indistinguishable downstream from a real one, so a source with a 20-request tier gets accounted
  against whatever the arm guessed. `RequireByCode` throws instead; there is no defensible default
  for another provider's budget or reset semantics.
- The map key is the friendly config key (`GoldApiIo`) rather than the source code, because keying
  by the code puts a dot in every environment variable name
  (`PriceSources__goldapi.io__ApiKey`) and the compose toolchain's dotenv parsers are inconsistent
  about those. The cost is that nothing structural stops two entries declaring the same
  `SourceCode` — they would collide on one ledger row — so the validator rejects duplicates.
- **`ValidateDataAnnotations()` does not descend into nested objects.** It evaluates the attributes
  on the options object's own properties and stops. `PriceSourcesOptionsValidator` does the descent
  explicitly and is what actually enforces `[Required]` on `ApiKey` and `[Range]` on
  `MonthlyRequestLimit`; while the sources hung off a nested property those attributes were dead,
  and a missing API key bound to the empty string, booted clean, and then spent the month one 401
  at a time (401 is not a quota rejection, so nothing clamps, and a lease is never refunded). Any
  new annotation on `PriceSourceOptions` is enforced by that validator, not by the `.Bind` chain.
- The validator also holds the cross-field checks the attributes cannot express: the
  cadence-vs-budget guard (formerly `PricePollingService.GuardPollBudget`, which ran after the host
  reported healthy and only covered the one hardcoded source) and the rule that
  `RollingThirtyDays` requires a `PeriodAnchor` — without one `ResolvePeriod` throws on *every*
  acquire, inside an HTTP handler, which the poller swallows and retries forever.
- A source with `Enabled: false` is exempt from the credential and cadence checks so a
  half-configured provider can sit in the file switched off. `SourceCode` is still required — it is
  the entry's identity.
- `appsettings.json` ships `ApiKey` as the empty string deliberately. A placeholder there would
  satisfy `[Required]` and put the hole straight back.

### Tests

`src/Aurum.Api.Tests` runs against a **real Postgres** via Testcontainers, not an in-memory or
SQLite provider — hypertables and vector columns behave differently enough that a mock would
validate nothing. One container per collection (`PostgresCollection`), migrated once; tests share
the database and clean up their own rows in `InitializeAsync`.

`InternalsVisibleTo` is set so tests can reach internal seams such as
`PostgresQuotaGovernor.ResolvePeriod` without widening the module's public surface.

`QuotaHandlerTests` drives the handler through a real `HttpClient` over a stub inner handler rather
than calling `SendAsync` directly, because `HttpClient` does its own exception handling on the way
out — a test that bypasses it would not prove `QuotaExhaustedException` actually reaches
`PricePollingService`'s catch clause. `Infrastructure/ListLogger.cs` captures log entries for the
cases where the log line *is* the deliverable; the governor's denial message is the only one so far.

Several tests exist to pin a decision rather than to find a bug — `Transport_failure_still_spends_the_lease`
is the executable form of "no refunds," which was otherwise enforced only by the absence of a
`catch`. Deleting one of those because it "tests nothing" removes the guard, not the redundancy.

Concurrency tests give each caller its own `DbContext` — a `DbContext` is not thread-safe, and the
concurrency being tested is between database transactions. A concurrency test that passes on the
first try should be checked for whether it is actually racing before it is believed.

## Conventions

- Decisions go in `ops/decisions/decisions.md` with the reasoning and the rejected alternatives, not
  only in code, and are tracked. The roadmap (`plans/aurum-phased-plan.md`) and requirements
  (`plans/aurum_brd.md`) are deliberately untracked — `plans/` is gitignored and exists only in the
  working copy, so a fresh clone will not have them. Ask for the contents rather than assuming the
  paths resolve.
- Comments explain *why* and non-obvious behaviour, aimed at a mid-to-senior engineer. Skip language
  basics and anything the code already states plainly.
- Prose in this repo consistently names the failure mode a decision avoids. Match that when editing
  docs or the decision log.
