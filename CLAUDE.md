# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Deeper notes live beside the code they describe and load when you work in that folder:
`src/Aurum.App.Infrastructure.Pricing/CLAUDE.md` (quota chain, configuration) and its `Cache/`,
`Deltas/` and `Macro/` folders, `src/Aurum.Api/Hubs/`, `src/Aurum.App.Application/Prices/`,
`src/Aurum.App.Infrastructure.Data/` and `src/Aurum.Api.Tests/`. The reasoning behind each rule is in
`ops/decisions/decisions.md` under the D-number given.

## Commands

```bash
dotnet build                                              # whole solution (Aurum.slnx)
dotnet test                                               # all tests — needs Docker
dotnet test --filter "FullyQualifiedName~QuotaGovernorTests"   # one class
dotnet test --filter "FullyQualifiedName~ResolvePeriod"        # one test; its class still starts Postgres
dotnet format                                             # before committing
docker compose up --build                                 # full stack: postgres, ollama, api

# migrations, from the repository root
dotnet ef migrations add <Name> --project src/Aurum.App.Infrastructure.Data
dotnet ef migrations list       --project src/Aurum.App.Infrastructure.Data
dotnet ef database update       --project src/Aurum.App.Infrastructure.Data
dotnet ef migrations script --idempotent --project src/Aurum.App.Infrastructure.Data
```

`~` in `--filter` is a substring match on the fully-qualified name.

- **No `export` and no `--startup-project`** for `dotnet ef`. `AurumDbContextFactory` supplies the
  connection; commands that connect (`database update`) need `AURUM_DESIGN_CONNECTION` in `.env`
  pointing at `Host=localhost`, because `.env`'s `Host=postgres` only resolves inside compose.
  Details in `src/Aurum.App.Infrastructure.Data/CLAUDE.md`.
- You rarely need `database update`: `Program.cs` migrates at startup.
- Tests require Docker. If `docker ps` is permission-denied, `sudo usermod -aG docker $USER` and log
  back in.

## Architecture

Five projects. The layer table in `docs/best-practices-api.md` is the contract.

```
src/
├── Aurum.App.SharedKernel/            ApiResponse<T>, CursorPage<T>, AuditableEntity, SupportedSymbol
├── Aurum.App.Infrastructure.Data/     AurumDbContext, Entities/, Migrations/, IUnitOfWork
├── Aurum.App.Infrastructure.Pricing/  Sources/, Quota/, Jobs/, Cache/, Deltas/, Realtime/ — the price feed;
│                                      Macro/ — the FRED and BLS clients, and FredImporter (D-22, D-23)
├── Aurum.App.Application/             Common/CQRS, Common/Behaviors, AppLogs/, Prices/
├── Aurum.Api/                         Program.cs (every registration), Controllers/, Hubs/ (SignalR)
└── Aurum.Api.Tests/
```

Three things that look wrong without the reason:

- **`Infrastructure.Pricing` is a fifth project the layer table does not list.** Price sources are
  external HTTP clients and the poller is a background job; neither fits "EF Core queries,
  repositories, DbContext". It depends on `Infrastructure.Data` because it writes four groups of
  tables: the quota governor `api_quota_windows`, the poller `price_ticks` and `price_sources`, the
  delta engine `price_events` (through `PriceEventSql`), and `FredImporter` `macro_series` and
  `macro_observations`. `LatestQuoteCache` and `DeltaEngine` also read `price_ticks` to warm up.
- **`Aurum.Api` references all four projects.** It is the composition root and must name every type
  it registers. The layer rule still holds for `Controllers/`: a controller reaching past
  `IMediator` into Infrastructure is the violation.
- **`AutoMapper` is deliberately absent.** Every version, 15.0.1 included, carries the unpatched
  advisory GHSA-rvv3-g6hj-g44x. Handlers map by hand. **`MediatR` is pinned to 12.5.0**: 13+ needs a
  paid licence key checked at runtime, so a bump fails the first `Send()` in production, not the
  build.

### Dependency injection — every registration is in `Program.cs`

No `Add<Module>Module` methods, no per-layer `ServiceCollectionExtensions`. **D-5 records both the
original module seam and its reversal** — read it before registering anything elsewhere.

- MediatR handlers and FluentValidation validators are found by assembly scanning (`AddMediatR()`,
  `AddValidatorsFromAssembly()`). Everything else, repositories included, is registered in
  `Program.cs`.
- `Infrastructure.Pricing` grants `InternalsVisibleTo("Aurum.Api")` so `Program.cs` can name its
  internal types: `QueryKeyAuthHandler`, `RegisteredPriceSource`, both options validators,
  `LatestQuoteCache`, `DeltaEngine` and `FailoverPriceFeed`. Nothing outside the module should
  resolve them, so don't make them public to satisfy wiring.
- **`SourceCircuitStore` is public** so the `/sources` endpoint can call `SnapshotAll()` (D-20), but
  its constructor and `For()` stay internal. The container only calls public constructors, so
  `Program.cs` registers it with a factory lambda.
- **`Program.AddPriceSource<T>` is a member of `Program`, not a local function**, so tests can call
  it. The order of two lines inside it — resilience handler above `QuotaHandler` — is what
  `ResilienceWiringTests` pins. Swapping them makes retries uncounted (see the Pricing CLAUDE.md).
- `services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>))`; `IAppLogQueue` is a
  **singleton** (a scoped queue drops rows when the
  request ends); `AppLogDrainService` batches rows into `app_logs`.
- `IRequestContextAccessor` is implemented by `HttpRequestContextAccessor` in `Aurum.Api`. The
  Application layer must not reference `IHttpContextAccessor`. Outside a request it yields
  `RequestContext.None`, which lets background jobs share `IAppLogService<T>`.

### Frontend (`app/`)

An Expo scaffold: `App.tsx`, `src/fixture/generateTicks.ts`, `src/fixture/rng.ts`. Charting is
settled on `lightweight-charts` (D-2); nothing is wired up.

**There is no Expo Router, Redux, NativeWind, Gluestack UI, `src/subscreens/`, `components/`,
`babel.config.js`, `jest.config.js` or `eslint.config.js` here.** `docs/best-practices.md` and
`docs/best-practices-redux.md` are the **target from the Phase 1 dashboard onward** (the next
frontend work; Phase 4 adds phone apps and alerts); examples marked "Found in the original app" come
from another project. Check a path exists before citing it.

### Data flow (CQRS with MediatR)

```
Request → Controller → IMediator.Send() → Pipeline Behaviors → Handler → Repository/DbContext → Database
              ApiResponse ← Controller ← Handler ← manual mapping ← Entities
```

Pipeline: LoggingBehavior → ValidationBehavior → TransactionBehavior (commands only) → Handler.
The DbContext is `AurumDbContext`. The only feature is `Prices` (queries only — see
`src/Aurum.App.Application/Prices/CLAUDE.md`). The only scheduled work is `PricePollingService`, a
`BackgroundService` outside MediatR. A feature with commands adds `Commands/`,
`Handlers/Commands/`, a validator per command and `Infrastructure.Data/Repositories/{Feature}/`;
`docs/cqrs-guide.md` shows the shape. **No new service layer** — handlers replace services.

### Controller rules

- Thin: receive request → `mediator.Send()` → return `ApiResponse<T>`
  (`SharedKernel/Common/ApiResponse.cs`). Never anonymous types.
- `sealed`, with `IMediator` + `IAppLogService<TController>` in a primary constructor. Actions
  return `ActionResult<ApiResponse<T>>` and start with `const string action = nameof(...)`.
  `PricesController` is the one to copy.
- Attributes: `[ApiController]`, `[Authorize]`, `[Route("api/v1/{feature}")]`,
  `[Produces("application/json")]`. (Phase 1 read endpoints are open — see the Prices CLAUDE.md.)
- Every action has try/catch, in this order: `ValidationException` → 400 (when a validator exists);
  `OperationCanceledException when (ct.IsCancellationRequested)` → rethrow; `Exception` →
  `LogErrorAsync(action, ex, ct: CancellationToken.None)` and
  `CreateError(ErrorMessages.UnexpectedErrorMessage)`.
- **Security-configuration actions log failures only — no success row.** None exist yet; add the
  enforcing test with the first one. Every other action logs success with
  `LogAsync(LogType.ActionLog, ...)`.
- Catch `FluentValidation.ValidationException` on any endpoint whose command or query has a
  validator → 400.
- `CancellationToken ct = default` on every async action. No business logic, DB access, external HTTP or
  `async void`.

### Handler rules

- One handler per command/query, implementing `IRequestHandler<TRequest, TResponse>`.
- **Business logic lives in the handler — never a pass-through to a service.** When moving logic out
  of a service, delete the interface, implementation and `Program.cs` registration, and move other
  consumers to `IMediator`.
- Query handlers may inject `AurumDbContext`, always with `AsNoTracking()`. Command handlers use
  `IUnitOfWork` or repositories; `TransactionBehavior` wraps them.
- Manual entity ↔ DTO mapping. Never return entities. DTOs never reference entities, and vice versa.
- Pass `CancellationToken` to every async call. No manual validation or transactions.
- No hardcoded status strings or IDs: use `SupportedSymbol.*` and each source's `SourceCode`
  constant (`GoldApiIoSource.SourceCode`). Constants live in `SharedKernel/Constants/`, one class per
  topic; add a class there if none fits. A constant that belongs to one class stays on it
  (`SourceCode`, an options `SectionName`, `private const`). Same on the frontend.

### Validator and repository rules

- **Every command must have a FluentValidation validator** (extends `AbstractValidator<TCommand>`)
  — a blocker. One per command in
  `Application/{Feature}/Validators/{CommandName}Validator.cs`, auto-discovered. Queries usually
  need none. **A validator never ships without its test file.**
- Repositories take `AurumDbContext` only, return entities only, live in `Repositories/{Feature}/`,
  and use `AsNoTracking()` for **every** read-only query, including ones used by query handlers.

## Invariants that are easy to break from outside their folder

Each folder's CLAUDE.md has the full reasoning. These are the ones a change elsewhere can break:

- **GoldAPI's free tier is ~100 requests a month.** Nothing that serves a web request may call a
  price service. Every retry attempt spends quota.
- **`PostgresQuotaGovernor.AcquireAsync` stays one raw SQL upsert.** Don't rewrite it in EF (D-7).
- **The injected `TimeProvider` stamps every time the app writes.** `now()` never appears in the app's
  SQL (only the migration seeds use it, outside the host's clock); raw SQL passes
  `CreatedAt`/`UpdatedAt` itself.
- **Poll order is memory → tick save → publish → events** (D-16, D-18, D-19).
- **`PriceTick.Retention` equals the migration's `INTERVAL '30 days'`.**
- **Single instance only.** Startup migration, the poller, the event waiting period and SignalR
  groups all assume one process.
- Compose forwards only the variables listed under `api.environment`; a new `.env` setting without a
  line there never reaches the container.

## Conventions

- Decisions go in `ops/decisions/decisions.md` with reasoning and rejected alternatives. `plans/` is
  gitignored (roadmap `aurum-phased-plan.md`, requirements `aurum_brd.md`); ask for the contents
  rather than assuming the paths resolve.
- Comments explain *why* and non-obvious behaviour for a mid-to-senior engineer.
- Prose names the failure mode a decision avoids. Match that in docs and the decision log.
- REST: `GET/POST /api/v1/{resources}`, `GET/PUT/DELETE /api/v1/{resources}/{id}`. No verbs,
  lowercase.
- Logging: always `IAppLogService<T>`, not `ILogger<T>`. Auto-enriched (UserId, TenantId, IpAddress,
  UserAgent, RequestPath, HttpMethod, CorrelationId), queued, non-blocking. Success:
  `LogAsync(LogType.ActionLog, action, details, 200)`. Error: `LogErrorAsync(action, exception, 500)`.

## Testing (backend)

xUnit + Moq in `src/Aurum.Api.Tests/`, against a real Postgres via Testcontainers — never an
in-memory or SQLite provider. **Every new CQRS feature needs controller, handler and validator test
files.** Naming, required cases per endpoint and per-class notes are in that folder's CLAUDE.md.
