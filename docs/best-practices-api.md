# Aurum - API Architecture & Best Practices
 
> Where to put your code, how each layer works, and the patterns every PR should follow.
 
---
 
## Architecture Layers
 
| Layer | Project | What It Does | Depends On | Never Does |
|-------|---------|-------------|------------|------------|
| **Presentation** | `Aurum.Api` | HTTP routing, auth, `ApiResponse<T>`, dispatches via `IMediator` | Application, SharedKernel | Business logic, DB access, HTTP calls to external APIs |
| **Application** | `Aurum.App.Application` | Commands, queries, handlers, validators, entity-to-DTO mapping | Infrastructure.Data, SharedKernel | Direct HTTP/controller concerns |
| **Data Access** | `Aurum.App.Infrastructure.Data` | EF Core queries, repositories, DbContext | SharedKernel | DTOs, business logic, mapping |
| **Shared** | `Aurum.App.SharedKernel` | Cross-cutting types (`ApiResponse<T>`, pagination) | Nothing | Feature-specific code |
 
---
 
## CQRS with MediatR (Preferred Pattern)
 
All **new** controller endpoints must use CQRS with MediatR.
 
### How It Works
 
```
Controller → IMediator.Send() → LoggingBehavior → ValidationBehavior → TransactionBehavior* → Handler
                                                                         (* commands only)
```
 
- **LoggingBehavior** — logs every request to DB via `IAppLogService`, warns on >500ms
- **ValidationBehavior** — runs FluentValidation validators, throws `ValidationException` on failure
- **TransactionBehavior** — wraps `ICommand<>` in `IUnitOfWork.ExecuteInTransactionAsync()`, skips queries
 
### Command vs Query
 
| If the endpoint... | Use | Transaction? |
|---|---|---|
| Reads data (GET) | `IQuery<TResponse>` | No |
| Creates, updates, or deletes (POST/PUT/DELETE) | `ICommand<TResponse>` | Yes (automatic) |
 
### Request Types
 
```csharp
// Query — read-only, no transaction
public record GetWidgetByIdQuery(int Id) : IQuery<WidgetDto?>;
 
// Command — mutates state, wrapped in transaction
public record CreateWidgetCommand(CreateWidgetDto Data) : ICommand<WidgetDto>;
```
 
- Use `record` types — immutable with value equality
- Nullable response (`WidgetDto?`) for single-item lookups
- Paginated queries return `PageResult<T>`
 
### No DI Registration Needed
 
MediatR auto-discovers handlers and FluentValidation auto-discovers validators via assembly scanning from `Aurum.App.Application`. Just create the files.
 
> **Full guide with examples:** `docs/cqrs-guide.md`
 
---
 
## Folder Structure Per Feature
 
Every feature follows this layout. Example using **AppLogs** (CQRS):
 
```
Aurum.App.Api/
  Controllers/
    AppLogsController.cs                ← thin, dispatches via IMediator
 
Aurum.App.Application/
  AppLogs/
    Commands/
      CreateLogCommand.cs               ← command record
    Queries/
      GetLogsQuery.cs                   ← query record
      GetLogByIdQuery.cs
    Handlers/
      Commands/
        CreateLogCommandHandler.cs      ← business logic
      Queries/
        GetLogsQueryHandler.cs
        GetLogByIdQueryHandler.cs
    Validators/
      CreateLogCommandValidator.cs      ← FluentValidation
    DTOs/
      AppLogDto.cs                      ← response DTOs
      CreateAppLogDto.cs
    Mappings/
      AppLogMappingProfile.cs           ← AutoMapper profile
 
Aurum.App.Infrastructure.Data/
  Repositories/
    AppLogs/                            ← (if needed for complex queries)
  Entities/
    Auth/AppLog.cs                      ← database entity
```
 
**Key rules:**
- Feature DTOs live in `Aurum.App.Application/{Feature}/DTOs/` — never in SharedKernel, never in Infrastructure
- Repository interface + implementation live in `Aurum.App.Infrastructure.Data/Repositories/{Feature}/`
- One AutoMapper profile per feature in `Aurum.App.Application/{Feature}/Mappings/`
- Validators live in `Aurum.App.Application/{Feature}/Validators/`
 
---
 
## Data Flow
 
### CQRS Pattern (preferred)
 
```
Request → Controller → IMediator.Send() → Pipeline Behaviors → Handler → Repository/DbContext → Database
                                                                   ↕
              ApiResponse ← Controller ← Handler ← AutoMapper ← Entities
```
 
| From → To | What Flows |
|-----------|-----------|
| Controller → Mediator | Command or Query record |
| Handler → Repository/DbContext | Entities, query parameters |
| Repository/DbContext → Handler | Entities (raw DB data) |
| Handler → Controller | DTOs (mapped from entities) |
| Controller → Client | `ApiResponse<DTO>` |
 

**Boundaries (both patterns):**
- DTOs never reference entity classes
- Entities never reference DTOs
- Entity-to-DTO mapping happens in the **handler** (CQRS) — never in the controller
- Repositories return entities only
- Query handlers may inject `ApplicationDbContext` directly for read-only queries (accepted CQRS exception)
 
---
 
## Controller Pattern
 
### Class Structure
 
Every controller must have:
- `[ApiController]`, `[Authorize]`, `[Route("api/v1/{feature}")]`, `[Produces("application/json")]`
- `IMediator` for dispatching commands and queries (preferred)
- `IAppLogService<TController>` for error logging in catch blocks
- `CancellationToken` on every async action
 
### What a Controller Does
 
1. Receives the HTTP request
2. Dispatches via `_mediator.Send()` (preferred)
3. Returns `ApiResponse<T>`
 
**Note:** The `LoggingBehavior` pipeline writes a generic success/error row for every MediatR request automatically. Per `CLAUDE.md` ("every controller action MUST log success AND error") the controller layer
*additionally* logs on both paths with richer, action-specific detail (entity IDs, row counts, cache hits) that the generic behavior can't capture — so a request typically produces the behavior's row plus the action's row. This redundancy is intentional
 and `CLAUDE.md` is authoritative: **keep the explicit `_appLogService.LogAsync(...)` success + error calls in every action**.
 
### What a Controller Does NOT Do
 
- Business logic (calculations, decisions, data transformation)
- HTTP calls to external APIs
- JSON serialization/deserialization of payloads
- Database access (no DbContext, no IDataService, no raw SQL)
- Parallel processing / task orchestration
- SOAP/XML construction
 
If your controller method is longer than ~15 lines (excluding the try/catch), the logic probably belongs in a handler or service.
 
### Example Pattern (CQRS — preferred)
 
```csharp
[HttpGet("{id}")]
public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken = default)
{
    try
    {
        var result = await _mediator.Send(new GetWidgetByIdQuery(id), cancellationToken);
 
        if (result == null)
            return NotFound(ApiResponse<WidgetDto>.CreateError("Not found", 404));
 
        return Ok(ApiResponse<WidgetDto>.CreateSuccess(result));
    }
    catch (Exception ex)
    {
        await _appLogService.LogErrorAsync("GetWidgetByIdError", ex, 500, cancellationToken);
        return StatusCode(500, ApiResponse<WidgetDto>.CreateError("Failed to get widget", 500));
    }
}
 
[HttpPost]
public async Task<IActionResult> Create(
    [FromBody] CreateWidgetDto dto, CancellationToken cancellationToken = default)
{
    try
    {
        var result = await _mediator.Send(new CreateWidgetCommand(dto), cancellationToken);
        return Ok(ApiResponse<WidgetDto>.CreateSuccess(result, "Created successfully", 201));
    }
    catch (FluentValidation.ValidationException ex)
    {
        var errors = ex.Errors.Select(e => e.ErrorMessage).ToList();
        return BadRequest(ApiResponse<object>.CreateError(string.Join("; ", errors), 400));
    }
    catch (Exception ex)
    {
        await _appLogService.LogErrorAsync("CreateWidgetError", ex, 500, cancellationToken);
        return StatusCode(500, ApiResponse<WidgetDto>.CreateError("Failed to create widget", 500));
    }
}
```
 
---
 
## Application Logging — `IAppLogService<T>`
 
This is my standard logging pattern for controllers. It writes structured audit/action logs to the database via a background queue.
 
### How It Works
 
- **Generic type**: `IAppLogService<YourClass>` — auto-captures the source class name (controller or service)
- **Auto-enriched fields**: UserId, TenantId, IpAddress, UserAgent, RequestPath, HttpMethod, CorrelationId (all pulled from HttpContext)
- **Non-blocking**: logs are queued asynchronously via `IAppLogQueue`, not written inline
- **Used in controllers and services** — always use `IAppLogService<T>`, not `ILogger<T>`
 
### Logging Rules
 
| Scenario | Method | Example |
|----------|--------|---------|
| **Success** | `LogAsync(LogType.ActionLog, action, details, statusCode)` | `_appLogService.LogAsync(LogType.ActionLog, "GetProspects", "Count: 25", 200)` |
| **Error** | `LogErrorAsync(action, exception, statusCode)` | `_appLogService.LogErrorAsync("GetProspectsError", ex, 500)` |
| **Error (alt)** | `LogAsync(LogType.ActionLog, action, errorDetails, 500)` | `_appLogService.LogAsync(LogType.ActionLog, "GetProspectsError", $"Error: {e.Message}", 500)` |
 
### What to Include in Details
 
- Error context: `$"Error: {e.GetType().Name}: {e.Message}"`
 
### Where Each Logging Tool Is Used
 
| Layer | Logging Tool | Purpose |
|-------|-------------|---------|
| **Controller** | `IAppLogService<T>` | Audit trail — who did what, when, from where |
| **Service** | `IAppLogService<T>` | Action logging — success/error tracking for business operations |
| **Repository** | None (usually) | Errors bubble up to service/controller |
 
---
 
## Handler Rules (CQRS — preferred)
 
Handlers are the CQRS equivalent of services. Each handler handles exactly one command or query.
 
- Implements `IRequestHandler<TRequest, TResponse>` from MediatR
- **Query handlers**: may inject `ApplicationDbContext` directly for read-only queries — always use `AsNoTracking()`
- **Command handlers**: inject repository interfaces (via `IUnitOfWork`) for the entity they own. The one accepted exception: a handler that reads from one aggregate and writes to another (e.g. reading a template to validate/snapshot
 it, then writing a run against a different aggregate) may inject `ApplicationDbContext`
*alongside* `IUnitOfWork` — the context is for the read-only cross-aggregate lookup and set-based `ExecuteUpdateAsync`/`ExecuteDeleteAsync` calls a generic repository can't express; all writes to the handler's own aggregate still go through `IUnitOfWork`/repositories
 inside `ExecuteInTransactionAsync`. Document the dual dependency with an XML comment on the class (see `GenerateLettersCommandHandler`, `DispatchLetterQueueCommandHandler`). This is not a license to bypass the repository for a handler's primary aggregate.
- Owns all **entity ↔ DTO mapping** (via AutoMapper or manual mapping)
- All `Handle` methods receive `CancellationToken` from MediatR — pass it to every async call
- Never returns raw entities — always DTOs
- No manual validation — use FluentValidation validators (auto-run by `ValidationBehavior`)
- No manual transaction management — `TransactionBehavior` wraps commands automatically
- No manual logging — `LoggingBehavior` handles success/error logging automatically
 
### Validator Rules
 
- One validator per command: `Aurum.App.Application/{Feature}/Validators/{CommandName}Validator.cs`
- Extends `AbstractValidator<TCommand>`
- Auto-discovered by assembly scanning — no DI registration needed
- Queries typically don't need validators
 
> **For new code**, use CQRS handlers instead of services. See `docs/cqrs-guide.md`.
 
---
 
## Repository Layer Rules
 
- Takes `ApplicationDbContext` as its sole constructor dependency
- Returns **entities only** — never DTOs
- Uses `AsNoTracking()` for read-only queries
- Each feature gets its own subfolder: `Repositories/{Feature}/`
- Interface + implementation live in the same subfolder
- Complex joins, filtering, sorting, and pagination happen here
 
---
 
## DI Registration
 
| What | Where | Pattern |
|------|-------|---------|
| **MediatR handlers** | Auto-discovered | Assembly scanning via `AddMediatR()` — no manual registration |
| **FluentValidation validators** | Auto-discovered | Assembly scanning via `AddValidatorsFromAssembly()` — no manual registration |
| **Repositories** | `Aurum.App.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs` | `services.AddScoped<IFeatureRepository, FeatureRepository>()` |
| **Services (legacy)** | `Aurum.App.Api/Program.cs` | `builder.Services.AddScoped<IFeatureService, FeatureService>()` |
| **AppLogService** | Already registered globally | `services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>))` |
| **Generic Repository** | Already registered globally | `services.AddScoped(typeof(IRepository<>), typeof(Repository<>))` |
 
---
 
## Response Wrapper — `ApiResponse<T>`
 
**Location:** `Aurum.App.Application/Common/DTOs/ApiResponse.cs`
 
Always use `ApiResponse<T>` for responses. Never return anonymous types (`new { ... }`) or plain strings.
 
| Scenario | Usage |
|----------|-------|
| Success with data | `Ok(ApiResponse<T>.CreateSuccess(data))` |
| Success with message | `Ok(ApiResponse<string>.CreateSuccess("Updated successfully"))` |
| Not found | `NotFound(ApiResponse<T>.CreateError("Not found", 404))` |
| Validation error | `BadRequest(ApiResponse<T>.CreateError("Invalid input", 400))` |
| Server error | `StatusCode(500, ApiResponse<T>.CreateError("Failed", 500))` |
 
---
 
## RESTful URL Conventions
 
| Action | Method | URL Pattern | Example |
|--------|--------|-------------|---------|
| Get all | `GET` | `/api/v1/{resources}` | `GET /api/v1/prospects` |
| Get one | `GET` | `/api/v1/{resources}/{id}` | `GET /api/v1/prospects/123` |
| Create | `POST` | `/api/v1/{resources}` | `POST /api/v1/referrals` |
| Update | `PUT` | `/api/v1/{resources}/{id}` | `PUT /api/v1/referrals/123` |
| Delete | `DELETE` | `/api/v1/{resources}/{id}` | `DELETE /api/v1/referrals/123` |
| Nested | `GET` | `/api/v1/{parent}/{id}/{child}` | `GET /api/v1/modules/1/screens` |
 
**Avoid:**
- Verbs in URLs: ~~`/check`~~, ~~`/getUser`~~, ~~`/createUser`~~
- Uppercase: ~~`/api/v1/Users`~~ (use lowercase)
- Actions that mutate data on `GET` — use `POST` or `PUT` instead
 
---
 
## HTTP Status Codes
 
| Status | When to Use | Example |
|--------|-------------|---------|
| `200 OK` | Success with data | GET, PUT success |
| `201 Created` | Resource created | POST success |
| `400 Bad Request` | Invalid input | Validation failed |
| `401 Unauthorized` | Not authenticated | No/invalid token |
| `403 Forbidden` | Not authorized | Lacks permission |
| `404 Not Found` | Resource missing | ID doesn't exist |
| `500 Server Error` | Unexpected error | Exception thrown |
 
---
 
## PR Checklist
 
### Controller
 
- [ ] Controller is thin — no business logic, no DB access, no external HTTP calls
- [ ] Uses `IMediator` to dispatch commands/queries (new code) or calls a service (legacy)
- [ ] Using `ApiResponse<T>` wrapper — no anonymous types (`new { ... }`), no raw entities
- [ ] `IAppLogService<T>` logging on **both** success and error paths (including binary endpoints like PDF)
- [ ] Catches `FluentValidation.ValidationException` on command endpoints → returns 400
- [ ] `CancellationToken cancellationToken = default` on every async action method
- [ ] No service locator (`HttpContext.RequestServices`) — all dependencies via constructor DI
- [ ] Mutation endpoints validate `userId != 0` before dispatching (use `ValidateUserId` helper)
- [ ] No `async void` — always `async Task`
 
### Handlers (CQRS)
 
- [ ] One handler per command/query — implements `IRequestHandler<TRequest, TResponse>`
- [ ] Returns **DTOs only** — never raw entities
- [ ] Returns **typed DTOs** — never `object`, `List<object>`, or anonymous types
- [ ] Command handlers use **repository interfaces** for their own aggregate — `ApplicationDbContext` only for the documented cross-aggregate-read / set-based-update exception (see Handler Rules above)
- [ ] Query handlers use `AsNoTracking()` on all EF queries
- [ ] Entity → DTO mapping done in handler (not in controller) — via AutoMapper or manual
- [ ] `CancellationToken` passed to all async calls
- [ ] No manual validation — use FluentValidation validator
- [ ] No manual transaction management — `TransactionBehavior` handles it
- [ ] Magic strings replaced with constants (`ReferralFileStatus.New`, `ReferralEntryType.Pdf`, etc.)
- [ ] Non-serializable types in command records (e.g. `Stream`) documented with XML comment
 
### Validators
 
- [ ] One validator per mutating command in `App.Application/{Feature}/Validators/`
- [ ] Extends `AbstractValidator<TCommand>`
- [ ] Covers required fields, length limits, format checks
- [ ] Validates `UserId > 0` for commands that require authentication
 
### Naming
 
- [ ] Command/Query naming matches interface: `ICommand<T>` → `*Command`, `IQuery<T>` → `*Query`
- [ ] Queries placed in `{Feature}/Queries/`, handlers in `{Feature}/Handlers/Queries/`
- [ ] Commands placed in `{Feature}/Commands/`, handlers in `{Feature}/Handlers/Commands/`
 
### Repository
 
- [ ] Returns entities only — no DTOs
- [ ] Interface + implementation in `Repositories/{Feature}/`
- [ ] `AsNoTracking()` on read queries
- [ ] Batch operations (e.g. `CreateReferralsBatchAsync`) added to interface when needed — handlers never use DbContext directly
 
### Tests (Required)
 
- [ ] Controller test file in `Controllers/{ControllerName}Tests.cs` — mock `IMediator`, test every endpoint (success, not found, error)
- [ ] Handler test file in `Handlers/{Feature}CommandHandlerTests.cs` — test success, not found, verify SaveChanges
- [ ] Query handler test file in `Handlers/{Feature}QueryHandlerTests.cs` — test data returned, empty, null
- [ ] Validator test file in `Validators/{Feature}CommandValidatorTests.cs` — test valid passes, required fields fail, boundary values
 
### Frontend Contract
 
- [ ] Every new backend DTO has a corresponding TypeScript `interface` in the frontend service file
- [ ] TypeScript interfaces have JSDoc comment naming the C# class and noting camelCase serialization
- [ ] No dead/unused TypeScript interfaces left behind after refactoring
 
### General
 
- [ ] RESTful URLs — no verbs, lowercase, proper HTTP methods
- [ ] Proper HTTP status codes
- [ ] DTOs in `Aurum.App.Application/{Feature}/DTOs/` — never reference entity classes
- [ ] Constants in `Aurum.App.Application/{Feature}/{Feature}Constants.cs` — no magic strings in handlers
- [ ] DI registered for new repositories (handlers/validators are auto-discovered)
- [ ] No `async void` anywhere
 
---
