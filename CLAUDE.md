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
dotnet ef migrations add <Name>
dotnet ef migrations script --idempotent    # inspect before applying
```

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
  remarks and `ops/decisions/phase-0.md` (D-7) carry the full reasoning.

Related invariants:

- Period rollover needs no scheduled job. A new period means a new `PeriodKey`, so no row exists and
  the next acquire creates one. Spent rows are never reset — they are history.
- `QuotaStatus.Limit - Used` is **not** remaining budget. When `ProviderRejected` is set, remaining
  is zero regardless of the counter.
- `PollInterval` and `MonthlyRequestLimit` are coupled: `PricePollingService.GuardPollBudget`
  refuses to start on a cadence that would overspend the period. Changing one usually means changing
  the other.
- The injected `TimeProvider` is the sole authority for period boundaries; `now()` never appears in
  governor SQL. Note that raw SQL also bypasses `AurumDbContext.ApplyAuditFields`, so `CreatedAt` /
  `UpdatedAt` must be passed explicitly there.

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

Options bind from the `PriceSources` section with `ValidateDataAnnotations().ValidateOnStart()`, so
a bad value fails the process at boot rather than at the first poll. Values come from
`appsettings.json` and are overridden by environment variables using `__` as the section separator
(`PriceSources__GoldApiIo__MonthlyRequestLimit`), which is how `docker-compose.yml` and `.env` set
them.

### Tests

`src/Aurum.Api.Tests` runs against a **real Postgres** via Testcontainers, not an in-memory or
SQLite provider — hypertables and vector columns behave differently enough that a mock would
validate nothing. One container per collection (`PostgresCollection`), migrated once; tests share
the database and clean up their own rows in `InitializeAsync`.

`InternalsVisibleTo` is set so tests can reach internal seams such as
`PostgresQuotaGovernor.ResolvePeriod` without widening the module's public surface.

Concurrency tests give each caller its own `DbContext` — a `DbContext` is not thread-safe, and the
concurrency being tested is between database transactions. A concurrency test that passes on the
first try should be checked for whether it is actually racing before it is believed.

## Conventions

- Decisions go in `ops/decisions/phase-<n>.md` with the reasoning and the rejected alternatives, not
  only in code. `plans/aurum-phased-plan.md` is the roadmap; `plans/aurum_brd.md` the requirements.
- Comments explain *why* and non-obvious behaviour, aimed at a mid-to-senior engineer. Skip language
  basics and anything the code already states plainly.
- Prose in this repo consistently names the failure mode a decision avoids. Match that when editing
  docs or the decision log.
