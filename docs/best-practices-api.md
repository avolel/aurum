# Aurum — how the API code is organised

> Where to put your code, what each layer does, and the rules every change should follow.

This guide describes the target shape of the API. Some of it isn't built yet: there are no controllers,
no feature repositories, no generic repository and no `ValidateUserId` helper so far. Where a rule
refers to something that doesn't exist yet, it says so. For what exists today, see CLAUDE.md.

A few terms used throughout:

- **Controller**: the class that receives a web request and sends back a response.
- **Command**: a request that changes data (create, update, delete).
- **Query**: a request that only reads data.
- **Handler**: the class that does the work for exactly one command or query.
- **DTO** (data transfer object): a plain class shaped for what the API sends or receives. It's kept
  separate from the database classes on purpose.
- **Entity**: a class that maps to a database table.
- **Repository**: a class that reads and writes one kind of entity.
- **MediatR**: the library that takes a command or query and finds the right handler for it.
- **FluentValidation**: the library used to write input checking rules.

---

## The layers

Each layer is its own project. A project can only use the projects listed in its "Uses" column, so
breaking a rule below usually won't compile.

| Layer | Project | What it does | Uses | Never does |
|-------|---------|--------------|------|------------|
| **Web** | `Aurum.Api` | Receives web requests, checks logins, wraps answers in `ApiResponse<T>`, hands work to MediatR | Application, SharedKernel | Business rules, database access, calls to outside web services |
| **Application** | `Aurum.App.Application` | Commands, queries, handlers, validators, turning entities into DTOs | Infrastructure.Data, SharedKernel | Anything to do with web requests or controllers |
| **Database** | `Aurum.App.Infrastructure.Data` | Database queries, repositories, the database context (`AurumDbContext`) | SharedKernel | DTOs, business rules, turning entities into DTOs |
| **Shared** | `Aurum.App.SharedKernel` | Small types every layer uses (`ApiResponse<T>`, `PageResult<T>`) | Nothing | Anything specific to one feature |

Two differences in this repository, explained in D-5 of `ops/decisions/decisions.md`:

- There's a fifth project, `Aurum.App.Infrastructure.Pricing`, for the outside price services and the
  price timer. It doesn't fit any row above.
- `Aurum.Api` actually uses all four other projects, because `Program.cs` is where everything is set
  up and it has to name everything. The "Uses" rule above still applies to the code in
  `Controllers/`: a controller must never go around MediatR to reach database or pricing code.

---

## Commands and queries, through MediatR

Every **new** endpoint must go through MediatR. The pattern of keeping reads (queries) and writes
(commands) apart is often called CQRS (command query responsibility segregation).

### What happens to a request

```
Controller → IMediator.Send() → LoggingBehavior → ValidationBehavior → TransactionBehavior* → Handler
                                                                         (* commands only)
```

Each "behavior" is a step every request passes through before reaching its handler:

- **LoggingBehavior**: writes one row per request to the `app_logs` table (through
  `IAppLogService`), and writes a warning to the console if the request took over 500 milliseconds.
- **ValidationBehavior**: runs the FluentValidation rules for that request, and throws a
  `ValidationException` if any fail.
- **TransactionBehavior**: for commands only, runs the handler inside a database transaction (so
  either all its changes are saved or none are), using `IUnitOfWork.ExecuteInTransactionAsync()`.
  Queries skip it.

### Command or query?

| If the endpoint... | Use | Transaction? |
|---|---|---|
| Reads data (`GET`) | `IQuery<TResponse>` | No |
| Creates, updates or deletes (`POST`, `PUT`, `DELETE`) | `ICommand<TResponse>` | Yes, automatically |

### Writing a request

Examples use `PriceSources`, the first feature planned for item 8. The names are illustrative; none of
these files exist yet.

```csharp
// Query: reads only, no transaction
public record GetPriceSourceByCodeQuery(string SourceCode) : IQuery<PriceSourceDto?>;

// Command: changes data, runs inside a transaction
public record SetPriceSourceEnabledCommand(string SourceCode, bool Enabled) : ICommand<PriceSourceDto>;
```

- Use `record` types. They can't be changed after they're created, and two with the same values count
  as equal.
- A lookup for one item returns a type that can be empty (`PriceSourceDto?`), for when nothing is found.
- Queries that return pages of results return `PageResult<T>`.

### Nothing to register

MediatR finds every handler, and FluentValidation finds every validator, by scanning the
`Aurum.App.Application` project at startup. Create the file and it's picked up.

> **Full guide with examples:** `docs/cqrs-guide.md`

---

## Folders for each feature

Every feature follows this layout. Example using `PriceSources`:

```
Aurum.Api/
  Controllers/
    PriceSourcesController.cs                  ← thin, hands work to MediatR

Aurum.App.Application/
  PriceSources/
    Commands/
      SetPriceSourceEnabledCommand.cs          ← the command record
    Queries/
      GetPriceSourcesQuery.cs                  ← the query record
      GetPriceSourceByCodeQuery.cs
    Handlers/
      Commands/
        SetPriceSourceEnabledCommandHandler.cs ← the business rules
      Queries/
        GetPriceSourcesQueryHandler.cs
        GetPriceSourceByCodeQueryHandler.cs
    Validators/
      SetPriceSourceEnabledCommandValidator.cs ← input checking rules
    DTOs/
      PriceSourceDto.cs                        ← what the API sends back

Aurum.App.Infrastructure.Data/
  Repositories/
    PriceSources/                              ← only if the queries are complex
      IPriceSourceRepository.cs
      PriceSourceRepository.cs
  Entities/
    Pricing/PriceSource.cs                     ← the database class
```

**Rules:**

- A feature's DTOs live in `Aurum.App.Application/{Feature}/DTOs/`. Never in SharedKernel, never in
  the database project.
- A repository's interface and class both live in `Aurum.App.Infrastructure.Data/Repositories/{Feature}/`.
- Validators live in `Aurum.App.Application/{Feature}/Validators/`.
- There's no `Mappings/` folder. AutoMapper isn't used here, because every published version has an
  unfixed high-severity security warning (see CLAUDE.md). Handlers copy entity fields into DTOs by hand.

---

## How data moves

```
Request → Controller → IMediator.Send() → Behaviors → Handler → Repository or AurumDbContext → Database
                                                         ↕
              ApiResponse ← Controller ← Handler ← copied by hand ← Entities
```

| From → To | What's passed |
|-----------|---------------|
| Controller → MediatR | A command or query record |
| Handler → Repository or database context | Entities, query values |
| Repository or database context → Handler | Entities (the raw database data) |
| Handler → Controller | DTOs (copied from the entities) |
| Controller → the caller | `ApiResponse<DTO>` |

**Boundaries:**

- DTOs never refer to entity classes.
- Entities never refer to DTOs.
- Turning entities into DTOs happens in the **handler**, never in the controller.
- Repositories return entities only.
- A query handler may use `AurumDbContext` directly for reads. That's an accepted exception to "go
  through a repository".

---

## Controllers

### What every controller has

- These four attributes: `[ApiController]`, `[Authorize]`, `[Route("api/v1/{feature}")]`,
  `[Produces("application/json")]`.
- `IMediator`, to send commands and queries.
- `IAppLogService<TController>`, to log errors in `catch` blocks.
- A `CancellationToken` on every async action, so work stops if the caller gives up.

### What a controller does

1. Receives the web request.
2. Sends a command or query with `_mediator.Send()`.
3. Returns an `ApiResponse<T>`.

**About logging twice:** `LoggingBehavior` already writes a generic success or error row for every
request. CLAUDE.md says every controller action must *also* log success and error itself, with details
the generic row can't know: IDs, row counts, whether something came from memory. So one request
usually produces two rows. That's on purpose. **Keep the explicit `_appLogService.LogAsync(...)` calls
for success and error in every action.**

The one exception is actions that change security settings: those log **failures only**. Check
CLAUDE.md before adding a success log to one.

### What a controller does NOT do

- Business rules (calculations, decisions, reshaping data)
- Calls to outside web services
- Turning JSON into objects or back, beyond what ASP.NET does automatically
- Database access (no database context, no raw SQL)
- Running work in parallel or coordinating tasks
- Building XML or SOAP messages

If an action is longer than about 15 lines, not counting the `try`/`catch`, the extra logic probably
belongs in a handler.

### Example

```csharp
// src/Aurum.Api/Controllers/PriceSourcesController.cs (illustrative; not built yet)

[HttpGet("{sourceCode}")]
public async Task<IActionResult> GetByCode(string sourceCode, CancellationToken cancellationToken = default)
{
    try
    {
        var result = await _mediator.Send(new GetPriceSourceByCodeQuery(sourceCode), cancellationToken);

        // Nothing found: 404, still wrapped in ApiResponse
        if (result == null)
            return NotFound(ApiResponse<PriceSourceDto>.CreateError("Not found", 404));

        return Ok(ApiResponse<PriceSourceDto>.CreateSuccess(result));
    }
    catch (Exception ex)
    {
        await _appLogService.LogErrorAsync("GetPriceSourceByCodeError", ex, 500, cancellationToken);
        return StatusCode(500, ApiResponse<PriceSourceDto>.CreateError("Failed to get price source", 500));
    }
}

[HttpPut("{sourceCode}")]
public async Task<IActionResult> SetEnabled(
    string sourceCode, [FromBody] bool enabled, CancellationToken cancellationToken = default)
{
    try
    {
        var result = await _mediator.Send(new SetPriceSourceEnabledCommand(sourceCode, enabled), cancellationToken);
        return Ok(ApiResponse<PriceSourceDto>.CreateSuccess(result, "Updated successfully"));
    }
    // Input failed a validator: 400 with the reasons
    catch (FluentValidation.ValidationException ex)
    {
        var errors = ex.Errors.Select(e => e.ErrorMessage).ToList();
        return BadRequest(ApiResponse<object>.CreateError(string.Join("; ", errors), 400));
    }
    catch (Exception ex)
    {
        await _appLogService.LogErrorAsync("SetPriceSourceEnabledError", ex, 500, cancellationToken);
        return StatusCode(500, ApiResponse<PriceSourceDto>.CreateError("Failed to update price source", 500));
    }
}
```

---

## App logging with `IAppLogService<T>`

This is the standard way to log from controllers and other classes. It writes action and audit rows to
the `app_logs` table, through a background queue.

### How it works

- **Named after its class**: `IAppLogService<PriceSourcesController>` records which class wrote the
  row.
- **Fills in details on its own**: user ID, tenant ID, IP address, browser, request path, HTTP method
  and correlation ID (an ID that ties together everything one request did). Outside a web request,
  such as in the price timer, these are left empty instead of causing an error.
- **Doesn't slow the request down**: rows go into a queue (`IAppLogQueue`), and a background service
  (`AppLogDrainService`) saves them in batches.
- **Use it instead of `ILogger<T>`** in controllers and other classes.

### Which method to call

| When | Method | Example |
|------|--------|---------|
| **Success** | `LogAsync(LogType.ActionLog, action, details, statusCode)` | `_appLogService.LogAsync(LogType.ActionLog, "GetPriceSources", "Count: 3", 200)` |
| **Error** | `LogErrorAsync(action, exception, statusCode)` | `_appLogService.LogErrorAsync("GetPriceSourcesError", ex, 500)` |
| **Error (other form)** | `LogAsync(LogType.ActionLog, action, errorDetails, 500)` | `_appLogService.LogAsync(LogType.ActionLog, "GetPriceSourcesError", $"Error: {e.Message}", 500)` |

### What to put in the details

- For errors: `$"Error: {e.GetType().Name}: {e.Message}"`

### Which layer logs what

| Layer | Tool | Why |
|-------|------|-----|
| **Controller** | `IAppLogService<T>` | Audit trail: who did what, when, and from where |
| **Handler** | Nothing by hand; `LoggingBehavior` does it | Success and error for every request |
| **Repository** | Usually nothing | Errors pass up to the handler and controller |

---

## Handlers

Each handler does the work for exactly one command or query.

- Implements MediatR's `IRequestHandler<TRequest, TResponse>`.
- **Query handlers** may use `AurumDbContext` directly for reads. Always add `AsNoTracking()`, which
  tells Entity Framework not to watch the results for changes, because they'll never be saved back.
- **Command handlers** use repositories (through `IUnitOfWork`) for the kind of entity they own.
  One accepted exception: a handler that reads one kind of entity and writes another may also use
  `AurumDbContext`, for that read and for bulk updates or deletes a repository can't express
  (`ExecuteUpdateAsync` or `ExecuteDeleteAsync`). Every write to the handler's own entity still goes
  through `IUnitOfWork` and repositories. Explain the two dependencies in a comment on the class. This
  isn't permission to skip the repository for the handler's own entity.
- Copies entity fields into DTOs by hand.
- Every `Handle` method gets a `CancellationToken` from MediatR. Pass it to every async call.
- Never returns an entity, always a DTO.
- No input checking by hand. Write a FluentValidation validator; `ValidationBehavior` runs it.
- No transactions by hand. `TransactionBehavior` wraps commands automatically.
- No logging by hand. `LoggingBehavior` logs success and error automatically.

### Validators

- One per command: `Aurum.App.Application/{Feature}/Validators/{CommandName}Validator.cs`.
- Extends `AbstractValidator<TCommand>`.
- Found automatically at startup, nothing to register.
- Queries usually don't need one.

> **For new code**, write handlers, not services. See `docs/cqrs-guide.md`.

---

## Repositories

- Takes `AurumDbContext` as its only constructor parameter.
- Returns **entities only**, never DTOs.
- Uses `AsNoTracking()` for reads.
- Each feature gets its own folder: `Repositories/{Feature}/`.
- The interface and the class live in the same folder.
- Complex joins, filtering, sorting and paging happen here.

---

## Setting things up (dependency injection)

"Dependency injection" means classes list what they need in their constructor, and the app hands it to
them at runtime. Here's where each kind of class is set up:

| What | Where | How |
|------|-------|-----|
| **MediatR handlers** | Automatic | Found by `AddMediatR()` scanning the Application project |
| **FluentValidation validators** | Automatic | Found by scanning the Application project |
| **Repositories** | `src/Aurum.Api/Program.cs` | `builder.Services.AddScoped<IPriceSourceRepository, PriceSourceRepository>()` |
| **`IUnitOfWork`** | `src/Aurum.Api/Program.cs` (already there) | `builder.Services.AddScoped<IUnitOfWork, UnitOfWork>()` |
| **App logging** | `src/Aurum.Api/Program.cs` (already there) | `builder.Services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>))` |

**Everything that isn't found automatically is set up in `Program.cs`.** There's no separate setup file
per layer (D-5).

---

## The response wrapper: `ApiResponse<T>`

**Location:** `src/Aurum.App.SharedKernel/Common/ApiResponse.cs`

Always return an `ApiResponse<T>`. Never return an anonymous object (`new { ... }`) or a plain string.

| When | What to write |
|------|---------------|
| Success with data | `Ok(ApiResponse<T>.CreateSuccess(data))` |
| Success with a message | `Ok(ApiResponse<string>.CreateSuccess("Updated successfully"))` |
| Not found | `NotFound(ApiResponse<T>.CreateError("Not found", 404))` |
| Bad input | `BadRequest(ApiResponse<T>.CreateError("Invalid input", 400))` |
| Server error | `StatusCode(500, ApiResponse<T>.CreateError("Failed", 500))` |

---

## Web addresses

Addresses name *things*, and the HTTP method says what to do with them. (This style is called REST.)

| Action | Method | Address pattern | Example |
|--------|--------|-----------------|---------|
| Get all | `GET` | `/api/v1/{things}` | `GET /api/v1/pricesources` |
| Get one | `GET` | `/api/v1/{things}/{id}` | `GET /api/v1/pricesources/goldapi.io` |
| Create | `POST` | `/api/v1/{things}` | `POST /api/v1/alerts` |
| Update | `PUT` | `/api/v1/{things}/{id}` | `PUT /api/v1/pricesources/goldapi.io` |
| Delete | `DELETE` | `/api/v1/{things}/{id}` | `DELETE /api/v1/alerts/42` |
| Things that belong to a thing | `GET` | `/api/v1/{parent}/{id}/{child}` | `GET /api/v1/pricesources/goldapi.io/quota` |

The examples are made up to show the pattern; none of these endpoints exist yet.

**Avoid:**

- Verbs in addresses: ~~`/check`~~, ~~`/getUser`~~, ~~`/createUser`~~
- Capital letters: ~~`/api/v1/PriceSources`~~ (use lowercase)
- `GET` requests that change data. Use `POST` or `PUT` instead.

---

## HTTP status codes

| Status | When | Example |
|--------|------|---------|
| `200 OK` | Worked, with data | `GET` or `PUT` worked |
| `201 Created` | Something new was created | `POST` worked |
| `400 Bad Request` | The input was wrong | A validator failed |
| `401 Unauthorized` | Not logged in | Missing or invalid login token |
| `403 Forbidden` | Logged in, but not allowed | Lacks permission |
| `404 Not Found` | The thing doesn't exist | Unknown ID |
| `500 Server Error` | Something unexpected broke | An exception was thrown |

---

## Checklist for every change

### Controller

- [ ] Thin: no business rules, no database access, no calls to outside web services
- [ ] Sends commands and queries through `IMediator`
- [ ] Returns `ApiResponse<T>`: no anonymous objects, no entities
- [ ] Logs with `IAppLogService<T>` on **both** success and error (including endpoints that return
      files, such as PDFs). Exception: security settings actions log failures only.
- [ ] Catches `FluentValidation.ValidationException` on command endpoints and returns 400
- [ ] `CancellationToken cancellationToken = default` on every async action
- [ ] Gets everything it needs through its constructor, never by asking
      `HttpContext.RequestServices` for it
- [ ] Endpoints that change data check the user ID isn't 0 before sending the command. (The planned
      helper for this, `ValidateUserId`, doesn't exist yet.)
- [ ] No `async void`: always `async Task`

### Handlers

- [ ] One handler per command or query, implementing `IRequestHandler<TRequest, TResponse>`
- [ ] Returns **DTOs only**, never entities
- [ ] Returns **specific DTO types**, never `object`, `List<object>` or anonymous objects
- [ ] Command handlers use **repositories** for their own entity. `AurumDbContext` only for the
      documented exception (reading another kind of entity, or bulk updates; see Handlers above)
- [ ] Query handlers use `AsNoTracking()` on every query
- [ ] Entities copied into DTOs in the handler, not the controller
- [ ] `CancellationToken` passed to every async call
- [ ] No input checking by hand: use a validator
- [ ] No transactions by hand: `TransactionBehavior` does it
- [ ] No hard-coded strings or IDs: use constants such as `SupportedSymbol.Gold` or
      `GoldApiIoSource.SourceCode`
- [ ] If a command record holds something that can't be turned into JSON (such as a `Stream`), say so
      in a comment

### Validators

- [ ] One validator per command that changes data, in `Aurum.App.Application/{Feature}/Validators/`
- [ ] Extends `AbstractValidator<TCommand>`
- [ ] Covers required fields, length limits and formats
- [ ] Checks `UserId > 0` for commands that need a logged-in user

### Naming

- [ ] Names match the interface: `ICommand<T>` → `...Command`, `IQuery<T>` → `...Query`
- [ ] Queries in `{Feature}/Queries/`, their handlers in `{Feature}/Handlers/Queries/`
- [ ] Commands in `{Feature}/Commands/`, their handlers in `{Feature}/Handlers/Commands/`

### Repository

- [ ] Returns entities only, no DTOs
- [ ] Interface and class in `Repositories/{Feature}/`
- [ ] `AsNoTracking()` on reads
- [ ] Bulk operations are added to the repository interface when needed. Command handlers don't use
      the database context for their own entity.

### Tests (required)

- [ ] Controller tests in `Controllers/{ControllerName}Tests.cs`: fake `IMediator`, and test every
      endpoint for success, not found and error
- [ ] Command handler tests in `Handlers/{Feature}CommandHandlerTests.cs`: success, not found, and
      check that saving was called
- [ ] Query handler tests in `Handlers/{Feature}QueryHandlerTests.cs`: data returned, nothing
      returned, and missing ID
- [ ] Validator tests in `Validators/{Feature}CommandValidatorTests.cs`: good input passes, each
      required field fails when empty, and boundary values

### Matching the app

- [ ] Every new DTO has a matching TypeScript `interface` in the app's service file
- [ ] Each TypeScript interface has a comment naming the C# class, and noting that field names arrive
      in camelCase (`sourceCode`, not `SourceCode`)
- [ ] No leftover, unused TypeScript interfaces after a refactor

### General

- [ ] Addresses follow the rules above: no verbs, lowercase, the right HTTP method
- [ ] The right status codes
- [ ] DTOs in `Aurum.App.Application/{Feature}/DTOs/`, never referring to entities
- [ ] Constants in `Aurum.App.Application/{Feature}/{Feature}Constants.cs`: no hard-coded strings in
      handlers
- [ ] New repositories set up in `Program.cs` (handlers and validators are found automatically)
- [ ] No `async void` anywhere

---
