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

Migrations (run from the repository root):

```bash
dotnet ef migrations add <Name> --project src/Aurum.App.Infrastructure.Data
dotnet ef migrations list       --project src/Aurum.App.Infrastructure.Data
dotnet ef database update       --project src/Aurum.App.Infrastructure.Data
dotnet ef migrations script --idempotent --project src/Aurum.App.Infrastructure.Data
```

**No `export` and no `--startup-project`.** `AurumDbContextFactory` is an
`IDesignTimeDbContextFactory<AurumDbContext>`, and EF prefers it over building the application host
— so the tooling never runs `Program.cs`, never hits its connection-string guard, and does not need
to be told where the host is. It is design-time only; nothing in it runs in production.

It resolves the connection string in this order:

1. An exported `ConnectionStrings__Aurum`, so CI and existing habits keep working.
2. `AURUM_DESIGN_CONNECTION` from `.env`.
3. `ConnectionStrings__Aurum` from `.env`.

`.env` is a Docker Compose convention, not a .NET one: Compose reads it and injects it as container
environment, and `dotnet ef` on the host never sees it. The factory parses the nearest `.env`,
searching upward from the working directory, which is why the commands above work from the
repository root or from inside a project directory.

**`AURUM_DESIGN_CONNECTION` exists because the two connection strings are not interchangeable.**
`.env`'s `ConnectionStrings__Aurum` says `Host=postgres`, which resolves only inside the compose
network; anything that actually opens a connection — `database update`, `dbcontext script` — runs on
your machine, where that name fails DNS with nothing in the error pointing at the cause. Compose
publishes 5432, so the override is the same database at `Host=localhost`. Nothing in
`docker-compose.yml` maps `AURUM_DESIGN_CONNECTION` into a container, so it is tooling-only.
`migrations add` and `migrations script` never connect and do not need it.

`Microsoft.EntityFrameworkCore.Design` is referenced by both `Aurum.App.Infrastructure.Data` and
`Aurum.Api`. The second is no longer needed for the commands above, but `dotnet ef` still builds a
startup project when one is passed explicitly, and removing it makes that form fail confusingly.

You rarely need `database update` at all: `Program.cs` migrates at startup, so `docker compose up`
applies pending migrations to the existing volume by itself.

Tests require Docker. If `docker ps` is permission-denied, `sudo usermod -aG docker $USER` and log
back in — nothing runs without it.

## Architecture

Five projects. The layer table in `docs/best-practices-api.md` is the contract; this is where each
one lives and what is actually in it today.

```
src/
├── Aurum.App.SharedKernel/            ApiResponse<T>, PageResult<T>, AuditableEntity, SupportedSymbol
├── Aurum.App.Infrastructure.Data/     AurumDbContext, Entities/, Migrations/, IUnitOfWork
├── Aurum.App.Infrastructure.Pricing/  Sources/, Quota/, Jobs/ — the price feed
├── Aurum.App.Application/             Common/CQRS, Common/Behaviors, AppLogs/
├── Aurum.Api/                         Program.cs (every registration), Controllers/
└── Aurum.Api.Tests/
```

Three things about this layout are not in the docs and will look wrong without the reason:

- **`Aurum.App.Infrastructure.Pricing` is a fifth project the layer table does not list.** The price
  sources are external HTTP clients and the poller is a background job; neither is "EF Core queries,
  repositories, DbContext", which is what that table says `Infrastructure.Data` does and the only
  other infrastructure box it offers. `cqrs-guide.md` refers to `Aurum.App.Infrastructure.*`
  generically, and this is that. It depends on `Infrastructure.Data` because the quota governor
  writes `api_quota_windows`.
- **`Aurum.Api` references all four projects**, not the two the table gives it. It is the
  composition root and every registration is in `Program.cs`, so it has to be able to name every
  concrete type it registers. The table's rule still holds for *code in `Controllers/`*: a
  controller that reaches past `IMediator` into Infrastructure is the violation that rule is about.
- **`AutoMapper` is deliberately absent**, although `best-practices-api.md` and `cqrs-guide.md` both
  assume it. Every published version, 15.0.1 included, carries the unpatched high-severity advisory
  GHSA-rvv3-g6hj-g44x, so there is no version to upgrade to. Handlers map entity to DTO by hand
  until that has a fix. `MediatR` is pinned to 12.5.0 for a different reason: 13+ requires a paid
  licence key validated at runtime, so an unpinned bump fails the first `Send()` in production
  rather than the build.

### Dependency injection — every registration is in `Program.cs`

There is no `Add<Module>Module` extension method and no `ServiceCollectionExtensions` per layer.
`src/Aurum.Api/Program.cs` is the only file that registers anything. **D-5 records both the original
folder-module seam and its reversal**, including what the reversal costs — read it before adding a
registration anywhere else.

Two consequences you will hit immediately:

- `Aurum.App.Infrastructure.Pricing` grants `InternalsVisibleTo("Aurum.Api")`. `SourceCircuitStore`,
  `QueryKeyAuthHandler`, `RegisteredPriceSource` and both options validators stay `internal` because
  nothing outside the module should resolve them, and `Program.cs` now has to name them anyway.
  Making them public to satisfy a wiring concern is the larger leak.
- `Program.AddPriceSource<T>` is a **member of the `Program` class**, not a local function. A local
  function in top-level statements is unreachable from the test assembly, and the order of two lines
  inside that method — resilience handler above `QuotaHandler` — is what `ResilienceWiringTests`
  exists to pin. Registering a source without the handler compiles, works, and spends its budget
  uncounted.

### Frontend (`app/`)

An Expo scaffold, and little more: `App.tsx`, `src/fixture/generateTicks.ts` and
`src/fixture/rng.ts`. Charting is settled on `lightweight-charts` (D-2) and nothing is wired up yet.

**There is no Expo Router, no Redux, no NativeWind, no Gluestack UI, no `src/subscreens/`, no
`components/`, no `babel.config.js`, `jest.config.js` or `eslint.config.js` in this repository.**
`docs/best-practices.md` and `docs/best-practices-redux.md` describe a mature admin application and
are **aspirational here** — treat them as the target shape for Phase 4, not as a description of
anything you can import today. Check that a path exists before citing it.

### Data Flow (CQRS with MediatR)
 
```
Request → Controller → IMediator.Send() → Pipeline Behaviors → Handler → Repository/DbContext → Database
                                                                   ↕
              ApiResponse ← Controller ← Handler ← manual mapping ← Entities
```
 
Pipeline order: LoggingBehavior → ValidationBehavior → TransactionBehavior (commands only) → Handler

**The DbContext is `AurumDbContext`.** The docs in `docs/` call it `ApplicationDbContext`
throughout; no such type exists here.

**There are no controllers yet.** The behaviors, `IAppLogService<T>` and `ApiResponse<T>` are
registered and ready; the first controller lands with item 8. Until then the only thing running
on a schedule is `PricePollingService`, a `BackgroundService` that does not go through MediatR.
 
- Controllers dispatch via `_mediator.Send()` — never call services or repositories directly
- Handlers own all business logic and entity ↔ DTO mapping
- Query handlers may inject `AurumDbContext` directly for read-only queries
- Command handlers use `IUnitOfWork` for writes (TransactionBehavior wraps automatically)
- DTOs never reference entity classes; entities never reference DTOs
- Repositories return entities only
- **No NEW service layer** — handlers replace services for new work. See `docs/cqrs-guide.md`.
 
### Feature Folder Structure

Illustrated with `PriceSources`, the feature item 8 will add. The docs in `docs/` use `Prospects`
and `Referrals`; those belong to a different application and no such feature exists here. Note the
presentation project is `Aurum.Api`, not `Aurum.App.Api` — `best-practices-api.md`'s layer table is
right and `cqrs-guide.md` is wrong on that one line.
 
```
Aurum.Api/Controllers/PriceSourcesController.cs            ← thin, dispatches via IMediator
Aurum.App.Application/PriceSources/
  Commands/SetPriceSourceEnabledCommand.cs           ← ICommand<T> record
  Queries/GetPriceSourcesQuery.cs                    ← IQuery<T> record
  Handlers/Commands/SetPriceSourceEnabledCommandHandler.cs
  Handlers/Queries/GetPriceSourcesQueryHandler.cs
  Validators/SetPriceSourceEnabledCommandValidator.cs ← FluentValidation
  DTOs/PriceSourceDto.cs
Aurum.App.Infrastructure.Data/Repositories/PriceSources/
  IPriceSourceRepository.cs
  PriceSourceRepository.cs
```
 
### Controller Rules
 
- Must be thin: receive request → `_mediator.Send()` → return `ApiResponse<T>`
- Inject `IMediator` + `IAppLogService<TController>` (for error logging in catch blocks)
- Required attributes: `[ApiController]`, `[Authorize]`, `[Route("api/v1/{feature}")]`, `[Produces("application/json")]`
- Do NOT add `[Authorize(Roles = "Admin")]` to Fields, Modules, or Screens controllers — the permissions system queries these for all users
- Every action must have try/catch — catch logs via `_appLogService.LogErrorAsync()` and returns `ApiResponse<T>.CreateError()`
- **Check first whether the action is on the security-configuration surface.** If it is, it logs **failures only** — do NOT add a success row (see "AppLog is failure-only on the security-configuration surface" below; a governance test enforces
 this). Otherwise: every action must log success via `_appLogService.LogAsync(LogType.ActionLog, ...)`, not just errors
- Catch `FluentValidation.ValidationException` on command endpoints → return 400
- `CancellationToken` on every async action method
- No business logic, no DB access, no external HTTP calls, no `async void`
- Always use `ApiResponse<T>` wrapper (`Aurum.App.Application/Common/DTOs/ApiResponse.cs`) — never return anonymous types
 
### Handler Rules (replaces Service Layer)
 
- One handler per command/query — implements `IRequestHandler<TRequest, TResponse>`
- **Handlers must contain business logic directly — NEVER delegate to a service (no pass-through handlers)**
- After migrating logic from a service into handlers, DELETE the old service interface + implementation AND remove its DI registration from `Program.cs`
- Check for OTHER consumers of the deleted service — update them to use `IMediator` + the new command/query
- Query handlers: may inject `AurumDbContext` directly — always use `AsNoTracking()`
- Command handlers: inject `IUnitOfWork` or repositories — TransactionBehavior wraps automatically
- Owns all entity ↔ DTO mapping. **Manual** — AutoMapper is not referenced (see Architecture)
- All `Handle` methods receive `CancellationToken` — pass to every async call in the chain
- No manual validation — use FluentValidation validators (auto-run by `ValidationBehavior`)
- No manual transaction management — `TransactionBehavior` handles it
- Never returns raw entities — always DTOs
- Never hardcode status strings or IDs — use constants. Here that means `SupportedSymbol.*` and
  each source's `SourceCode` constant (`GoldApiIoSource.SourceCode`). The `ReferralFileStatus.*`
  / `AppointmentStatusIds.*` families the docs cite belong to a different application and do not
  exist in this repository. If a constants class doesn't exist for a domain, create one. The same
  applies to the frontend — named constants, not inline magic numbers.
 one. The same applies to frontend — define named constants (e.g., `APPOINTMENT_STATUS_SCHEDULED = 1`) rather than inline magic numbers.
 
### Validator Rules
 
- **Every command MUST have a FluentValidation validator** — this is a blocker, no exceptions
- One validator per command: `Aurum.App.Application/{Feature}/Validators/{CommandName}Validator.cs`
- Extends `AbstractValidator<TCommand>` from FluentValidation
- Auto-discovered by assembly scanning — no DI registration needed
- Queries typically don't need validators
- **Validator tests must be created alongside validators** — never create a validator without its test file
 
### Repository Layer Rules
 
- Takes `AurumDbContext` as its sole constructor dependency
- Returns entities only — never DTOs
- **Uses `AsNoTracking()` for ALL read-only queries** — any query where the entity is mapped to a DTO and never saved back MUST use `AsNoTracking()`. This includes repository methods used by query handlers.
- Each feature gets its own subfolder: `Repositories/{Feature}/`

### The quota chain — the part that needs multiple files to understand

GoldAPI's free tier is roughly **100 requests per month**. Everything below exists because an
in-memory counter that resets with the container can spend a month of budget in an afternoon while
every individual request looks fine.

```
PricePollingService  →  IPriceFeed (FailoverPriceFeed)
                            ├─ SourceCircuitStore → SourceCircuit (in-memory, per source)
                            └─ IPriceSource, in priority order
                                  └─ HttpClient (typed)
                                        └─ resilience pipeline   total timeout → retry → per-attempt timeout
                                              └─ QuotaHandler (DelegatingHandler)
                                                    └─ auth handler
                                                          └─ IQuotaGovernor → api_quota_windows
```

Read that top to bottom as the order a request passes through. Four placement decisions here look
arbitrary in isolation:

- **`QuotaHandler` is a `DelegatingHandler`, not a wrapper around `IPriceSource`.** The retries and
  the failover chain both sit above `IPriceSource`; a governor at that level would let every retry
  spend quota uncounted. The handler sees exactly what leaves the process.
- **The resilience pipeline is registered *before* `QuotaHandler`, so it is outermost.** Every retry
  attempt re-enters the handler and is charged its own lease, because that is what the provider
  bills. Swapping those two lines makes retries free in our ledger and billed by the provider — an
  under-count, the direction that costs a month rather than a poll.
  `ResilienceWiringTests.Each_retry_attempt_spends_its_own_lease` is the only thing that catches the
  swap, and it calls `Program.AddPriceSource<T>` rather than hand-composing the chain for exactly
  that reason.
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
- **The circuit breaker's state is the opposite rule from the governor's, one folder away.**
  `SourceCircuit` is in-memory, authoritative, and never rehydrated at startup; the governor is
  durable. A spent request is a fact about the provider's ledger that outlives our process, while a
  circuit is a fact about *our own recent observations*, and a fresh process has none. Loading an
  open circuit at boot would blind a new process to a healthy source for a break it never observed
  (D-14). `price_sources.LastFailureAt` / `LastFailureReason` is a projection for operators, written
  once per poll by `PricePollingService` and never read back.
- **Quota exhaustion is not a circuit fault.** The source is healthy and out of budget. The chain
  records `SourceAttemptOutcome.QuotaExhausted` and moves to the next source immediately; the
  breaker is not touched, because opening on it would keep the source out of the chain after its
  period rolls and its budget is fresh.
- **The poller sleeps only when *every* source is out of quota**, and then only to the *earliest*
  `ResetsAt`. A single fault or a single open circuit makes `AllSourcesFailedException.AllQuotaExhausted`
  false and the feed stays on cadence — those clear in minutes, and a month-long sleep over one is
  the failure item 3 exists to remove.
- **`PriceSourceOptions.Enabled` now actually filters.** `FailoverPriceFeed` builds the chain from
  configuration and joins it to the registered sources, so a disabled or unconfigured source is not
  called. Before item 3 the poller selected straight off `IEnumerable<IPriceSource>`, which has no
  `Enabled` to filter on, and setting it shortened the startup log while changing nothing.
- **`PriceSource.IsEnabled`, the entity column, has no reader and no writer.** Configuration is
  authoritative; the column is descriptive and seeded only. Item 8 serves `price_sources` over HTTP
  and is the point at which it is either wired as a projection or deleted. Do not toggle it in psql
  and expect anything to happen.
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

`InternalsVisibleTo` is set on `Aurum.App.Infrastructure.Data`, `Aurum.App.Infrastructure.Pricing`
and `Aurum.Api` so tests can reach internal seams — `PostgresQuotaGovernor.ResolvePeriod`,
`SourceCircuit`, `SourceCircuitStore`, `FailoverPriceFeed`, `Program.AddPriceSource<T>` — without
widening any module's public surface.

`ResilienceWiringTests` calls **the production registration**, `Program.AddPriceSource<T>`, and
swaps only the primary handler for a counting stub. The subject is the order of two lines inside
that method, so hand-composing the chain in the test would re-declare that order and then assert the
test agrees with itself. It uses `TimeProvider.System`, unlike everything else here:
`AddResilienceHandler` resolves `TimeProvider` from the container and hands it to Polly, so a
`FakeTimeProvider` makes the retry backoff wait on a clock nothing advances and the test hangs
rather than failing.

The circuit and chain tests (`SourceCircuitTests`, `FailoverPriceFeedTests`) need **no container**
and run in milliseconds. That is the payoff of `SourceCircuit` measuring time by comparing two
`TimeProvider` reads instead of holding a timer, and it is half of D-14's case against Polly's
breaker.

`PricePollingServiceTests` does need Postgres, because the `price_sources` projection is half its
subject. Its waits are the fragile part: `ScriptedPriceFeed.WaitForCallAsync` completes when the
feed is *entered*, and the poller writes the tick afterwards, so a test that asserts straight after
that wait races the save. `WaitUntilAsync` polls the database for the side effect instead.

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

  ### DI Registration
 
- **MediatR handlers**: auto-discovered via `AddMediatR()` assembly scanning
- **FluentValidation validators**: auto-discovered via `AddValidatorsFromAssembly()`
- **Everything else, including repositories**: `src/Aurum.Api/Program.cs`. There is no per-layer
  `ServiceCollectionExtensions` — see the DI section above and D-5
- **AppLogService**: `services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>))`, with
  `IAppLogQueue` as a **singleton** — it owns the channel, and a scoped queue would be a fresh empty
  channel per request whose rows are dropped when the scope ends — and `AppLogDrainService` as a
  hosted service that batches them into `app_logs`
- **`IRequestContextAccessor`**: implemented by `HttpRequestContextAccessor` in `Aurum.Api`. The
  Application layer must not reference `IHttpContextAccessor` directly — its "never does" is HTTP.
  Outside a request (the poller, the drain) it yields `RequestContext.None` rather than throwing,
  which is what lets a background job share one `IAppLogService<T>` with controllers

### RESTful URL Conventions
 
- `GET /api/v1/{resources}` (list), `GET /api/v1/{resources}/{id}` (detail)
- `POST /api/v1/{resources}` (create), `PUT /api/v1/{resources}/{id}` (update), `DELETE /api/v1/{resources}/{id}` (delete)
- No verbs in URLs, always lowercase
 
### Application Logging (`IAppLogService<T>`)
 
- Used in controllers AND services — always use `IAppLogService<T>`, not `ILogger<T>`
- Auto-enriched: UserId, TenantId, IpAddress, UserAgent, RequestPath, HttpMethod, CorrelationId
- Non-blocking: queued via `IAppLogQueue`
- Log success: `LogAsync(LogType.ActionLog, action, details, 200)`
- Log error: `LogErrorAsync(action, exception, 500)` or `LogAsync(LogType.ActionLog, action, errorDetails, 500)`
 
### Unit Testing (Backend)
 
**Framework**: xUnit + Moq. Tests in `src/Aurum.Api.Tests/`.
 
**Every new CQRS feature must have test files for: controller, handlers, and validators.** All three are required — not optional.
 
**Test file naming and location**:
 
- `Controllers/{ControllerName}Tests.cs` — controller tests
- `Handlers/{Feature}CommandHandlerTests.cs` — command handler tests
- `Handlers/{Feature}QueryHandlerTests.cs` — query handler tests
- `Validators/{Feature}CommandValidatorTests.cs` — validator tests
 
**Test method naming**: `{MethodName}_{Scenario}_{ExpectedResult}` (e.g., `Handle_WithValidId_ReturnsDto`)
 
#### Controller Tests
 
- Mock `IMediator` with `Mock<IMediator>` — never mock services directly
- Mock `IAppLogService<TController>` for logging
- Set up `ControllerContext` with `HttpContext` and user claims for auth tests
- Follow Arrange/Act/Assert pattern
- Test every endpoint for: success (200), not found (404 if applicable), error (500)
 
**What to test per endpoint type**:
 
| Endpoint  | Required Tests                                                     |
| --------- | ------------------------------------------------------------------ |
| GET by ID | 200 with valid ID, 404 with invalid ID, 500 on exception           |
| GET list  | 200 with data, 200 with empty list, 500 on exception               |
| POST      | 200/201 with valid input, 400 with invalid input, 500 on exception |
| PUT       | 200 with valid update, 404 not found, 500 on exception             |
| DELETE    | 200 with valid ID, 404 not found, 500 on exception                 |
 
**Common Moq patterns**:
 
- `_mockMediator.Setup(m => m.Send(It.IsAny<TCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(value)` — return data
- `_mockMediator.Setup(m => m.Send(It.IsAny<TQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((T?)null)` — return null
- `_mockMediator.Setup(m => m.Send(It.IsAny<TCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception(...))` — simulate error
- `It.IsAny<T>()` — match any parameter value
- `_mockMediator.Verify(m => m.Send(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()), Times.Once)` — verify call happened
 
#### Handler Tests
 
- **Command handlers**: Mock `IUnitOfWork` and `IRepository<T>`. Test success path (entity saved, correct return), not-found path (returns false/null), and verify `SaveChangesAsync` called.
- **Query handlers**: Mock `IRepository` or use in-memory EF Core (`UseInMemoryDatabase`). Test data returned, empty result, null for missing ID.
- Test file: `Handlers/{Feature}CommandHandlerTests.cs` and `Handlers/{Feature}QueryHandlerTests.cs`
 
#### Validator Tests
 
- Instantiate the validator directly (`new CreateXCommandValidator()`) — no mocking needed.
- Call `ValidateAsync(command)` and assert on `result.IsValid` and `result.Errors`.
- **Required tests per validator**: valid input passes, each required field empty fails, boundary values (max length exact, max length + 1).
- Test file: `Validators/{Feature}CommandValidatorTests.cs`
