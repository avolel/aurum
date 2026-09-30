# Commands and queries with MediatR: developer guide

## Overview

This project keeps requests that **read** data (queries) apart from requests that **change** data
(commands). The pattern is called CQRS (command query responsibility segregation). A library called
MediatR takes each command or query and passes it to the one class that handles it, called its
handler. Every new endpoint should follow this pattern.

Examples in this guide use `PriceSources`, the first feature planned for item 8. None of these files
exist yet, and the names are illustrative. Terms used here (controller, DTO, entity, repository) are
explained at the top of `docs/best-practices-api.md`.

### What happens to a request

```
Controller → IMediator.Send() → LoggingBehavior → ValidationBehavior → TransactionBehavior* → Handler
                                                                         (* commands only)
```

Each "behavior" is a step every request passes through before its handler:

- **LoggingBehavior**: writes one row per request to the `app_logs` table (through
  `IAppLogService`), and writes a console warning for anything slower than 500 milliseconds.
- **ValidationBehavior**: runs the FluentValidation rules for the request, and throws a
  `ValidationException` if any fail.
- **TransactionBehavior**: for commands only, runs the handler inside a database transaction (all its
  changes are saved, or none are), using `IUnitOfWork.ExecuteInTransactionAsync()`. Queries skip it.

**Whether a request gets a transaction depends only on its interface.** `TransactionBehavior` checks
whether the record implements `ICommand<T>`. It doesn't look at the HTTP method or the name. A record
that implements MediatR's plain `IRequest<T>` gets no transaction and looks the same everywhere else.
That's why the naming rule (rule 6 below) blocks a merge, and isn't only a style preference.

---

## Folders

For the `PriceSources` feature:

```
Aurum.App.Application/
  PriceSources/
    Commands/
      SetPriceSourceEnabledCommand.cs
      SetPriceSourcePriorityCommand.cs
    Queries/
      GetPriceSourcesQuery.cs
      GetPriceSourceByCodeQuery.cs
    Handlers/
      Commands/
        SetPriceSourceEnabledCommandHandler.cs
        SetPriceSourcePriorityCommandHandler.cs
      Queries/
        GetPriceSourcesQueryHandler.cs
        GetPriceSourceByCodeQueryHandler.cs
    Validators/
      SetPriceSourceEnabledCommandValidator.cs
      SetPriceSourcePriorityCommandValidator.cs
    DTOs/
      PriceSourceDto.cs
```

There's no `Services/` folder. Handlers replace services for all new work.

---

## Step by step: adding an endpoint

### 1. Decide: command or query?

| If the endpoint... | Use |
|---|---|
| Reads data (`GET`) | `IQuery<TResponse>` |
| Creates, updates or deletes data (`POST`, `PUT`, `DELETE`) | `ICommand<TResponse>` |

Commands run inside a database transaction automatically. Queries don't.

### 2. Write the request

**Query**: `Queries/GetPriceSourceByCodeQuery.cs`

```csharp
using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.PriceSources.DTOs;

namespace Aurum.App.Application.PriceSources.Queries;

public record GetPriceSourceByCodeQuery(string SourceCode) : IQuery<PriceSourceDto?>;
```

**Command**: `Commands/SetPriceSourceEnabledCommand.cs`

```csharp
using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.PriceSources.DTOs;

namespace Aurum.App.Application.PriceSources.Commands;

public record SetPriceSourceEnabledCommand(string SourceCode, bool Enabled) : ICommand<PriceSourceDto>;
```

**Rules:**

- Use `record` types. They can't be changed after they're created, and two with the same values count
  as equal, with no extra code.
- A lookup for one item returns a type that can be empty (`PriceSourceDto?`), for when nothing is found.
- Queries that return pages of results return `PageResult<T>`.
- Commands return whatever the result is (`PriceSourceDto`, `bool`, and so on).

### 3. Write the handler

**Query handler**: `Handlers/Queries/GetPriceSourceByCodeQueryHandler.cs`

```csharp
using Aurum.App.Application.PriceSources.DTOs;
using Aurum.App.Application.PriceSources.Queries;
using Aurum.App.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Application.PriceSources.Handlers.Queries;

public class GetPriceSourceByCodeQueryHandler(AurumDbContext context)
    : IRequestHandler<GetPriceSourceByCodeQuery, PriceSourceDto?>
{
    public async Task<PriceSourceDto?> Handle(GetPriceSourceByCodeQuery request, CancellationToken cancellationToken)
    {
        var entity = await context.PriceSources
            .AsNoTracking()   // read only: don't watch these rows for changes
            .FirstOrDefaultAsync(s => s.Code == request.SourceCode, cancellationToken);

        // Copy by hand. AutoMapper isn't used here (see CLAUDE.md).
        return entity is null
            ? null
            : new PriceSourceDto(entity.Code, entity.LastSuccessAt, entity.LastFailureAt);
    }
}
```

**Command handler**: `Handlers/Commands/SetPriceSourceEnabledCommandHandler.cs`

```csharp
using Aurum.App.Application.PriceSources.Commands;
using Aurum.App.Application.PriceSources.DTOs;
using Aurum.App.Infrastructure.Data.Repositories.PriceSources;
using MediatR;

namespace Aurum.App.Application.PriceSources.Handlers.Commands;

public class SetPriceSourceEnabledCommandHandler(IPriceSourceRepository repository)
    : IRequestHandler<SetPriceSourceEnabledCommand, PriceSourceDto>
{
    public async Task<PriceSourceDto> Handle(SetPriceSourceEnabledCommand request, CancellationToken cancellationToken)
    {
        var entity = await repository.GetByCodeAsync(request.SourceCode, cancellationToken)
            ?? throw new KeyNotFoundException(request.SourceCode);

        entity.IsEnabled = request.Enabled;

        // No SaveChanges here: TransactionBehavior saves and commits after the handler returns.
        return new PriceSourceDto(entity.Code, entity.LastSuccessAt, entity.LastFailureAt);
    }
}
```

`PriceSourceDto`'s fields and `IPriceSourceRepository.GetByCodeAsync` are made up for the example.
Note also that `PriceSource.IsEnabled` currently does nothing. The settings file decides which
services are switched on, and item 8 has to either hook the column up or delete it (see CLAUDE.md).

**Handler rules:**

- Query handlers may use `AurumDbContext` directly. Always add `AsNoTracking()`.
- Command handlers use repositories (see rule 9 below).
- Always pass `cancellationToken` to async calls.
- The handler holds the business rules, not the controller.
- Never return an entity. Always copy it into a DTO.

### 4. Add a validator (commands only)

`Validators/SetPriceSourceEnabledCommandValidator.cs`

```csharp
using Aurum.App.Application.PriceSources.Commands;
using FluentValidation;

namespace Aurum.App.Application.PriceSources.Validators;

public class SetPriceSourceEnabledCommandValidator : AbstractValidator<SetPriceSourceEnabledCommand>
{
    public SetPriceSourceEnabledCommandValidator()
    {
        RuleFor(x => x.SourceCode)
            .NotEmpty().WithMessage("Source code is required")
            .MaximumLength(50).WithMessage("Source code must not exceed 50 characters");
    }
}
```

Validators are found automatically when the app starts, so there's nothing to register.
`ValidationBehavior` runs them before the handler.

### 5. Write the controller

If you ever find an older controller that calls a service, this is the change. (There are no services
in this repository today.)

**Before: calls a service**

```csharp
public class PriceSourcesController : ControllerBase
{
    private readonly IPriceSourceService _priceSourceService;
    private readonly IAppLogService<PriceSourcesController> _appLogService;

    [HttpGet("{sourceCode}")]
    public async Task<IActionResult> GetByCode(string sourceCode, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _priceSourceService.GetByCodeAsync(sourceCode, cancellationToken);
            if (result == null) return NotFound(...);
            return Ok(result);
        }
        catch (Exception ex)
        {
            await _appLogService.LogErrorAsync(...);
            return StatusCode(500, ...);
        }
    }
}
```

**After: sends a query through MediatR, and wraps every answer in `ApiResponse<T>`**

```csharp
// src/Aurum.Api/Controllers/PriceSourcesController.cs
using MediatR;

[ApiController]
[Authorize]
[Route("api/v1/pricesources")]
[Produces("application/json")]
public class PriceSourcesController(
    IMediator mediator,
    IAppLogService<PriceSourcesController> appLogService) : ControllerBase
{
    [HttpGet("{sourceCode}")]
    public async Task<IActionResult> GetByCode(string sourceCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await mediator.Send(new GetPriceSourceByCodeQuery(sourceCode), cancellationToken);
            if (result == null)
                return NotFound(ApiResponse<PriceSourceDto>.CreateError("Not found", 404));

            await appLogService.LogAsync(LogType.ActionLog, "GetPriceSourceByCode", sourceCode, 200, cancellationToken);
            return Ok(ApiResponse<PriceSourceDto>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync("GetPriceSourceByCodeError", ex, 500, cancellationToken);
            return StatusCode(500, ApiResponse<PriceSourceDto>.CreateError("Failed to get price source", 500));
        }
    }

    [HttpPut("{sourceCode}")]
    public async Task<IActionResult> SetEnabled(
        string sourceCode, [FromBody] bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await mediator.Send(new SetPriceSourceEnabledCommand(sourceCode, enabled), cancellationToken);

            await appLogService.LogAsync(LogType.ActionLog, "SetPriceSourceEnabled", $"{sourceCode}={enabled}", 200, cancellationToken);
            return Ok(ApiResponse<PriceSourceDto>.CreateSuccess(result));
        }
        // A validator failed: 400 with the reasons
        catch (FluentValidation.ValidationException ex)
        {
            var errors = ex.Errors.Select(e => e.ErrorMessage).ToList();
            return BadRequest(ApiResponse<object>.CreateError(string.Join("; ", errors), 400));
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync("SetPriceSourceEnabledError", ex, 500, cancellationToken);
            return StatusCode(500, ApiResponse<PriceSourceDto>.CreateError("Failed to update price source", 500));
        }
    }
}
```

**Controller rules:**

- `IMediator` is the main thing the controller uses.
- `IAppLogService<TController>` logs success and error in every action (rule 4).
- Catch `FluentValidation.ValidationException` on command endpoints and return 400.
- Remove any input checking by hand that a validator now covers.
- Keep controllers thin: no business rules, only passing the request on and dealing with the web side.

**Check first whether the action changes security settings.** If it does, it logs **failures only**.
Don't add a success row: CLAUDE.md explains why, and a test enforces it.

### 6. Nothing to register

MediatR finds every handler, and FluentValidation finds every validator, in the `Aurum.App.Application`
project. Create the files and they work.

---

## Special cases

### Calling an outside service and writing to the database

**The transaction is already open when a command handler starts.** `TransactionBehavior` begins it
before calling the handler and commits it after the handler returns. So any call to an outside web
service inside a command handler happens while a database transaction is open.

Put the outside call **before** the database writes. A service that's slow to answer then holds the
transaction open without also holding any rows locked by writes:

```csharp
public async Task<PriceSourceDto> Handle(RefreshPriceSourceCommand request, CancellationToken cancellationToken)
{
    // 1. Call the outside service first. The transaction is open, but nothing is written yet.
    var status = await _providerClient.GetAccountStatusAsync(request.SourceCode, cancellationToken);

    // 2. Then change the database. TransactionBehavior saves and commits after the handler returns.
    var entity = await _repository.GetByCodeAsync(request.SourceCode, cancellationToken);
    entity!.LastSuccessAt = status.CheckedAt;

    return new PriceSourceDto(entity.Code, entity.LastSuccessAt, entity.LastFailureAt);
}
```

`RefreshPriceSourceCommand` and `_providerClient` are made up for the example.

**If database retries are ever switched on, this changes.** `UnitOfWork` runs the transaction inside
Entity Framework's retry wrapper (its "execution strategy"). Today that wrapper doesn't retry, because
`Program.cs` doesn't switch retries on (`EnableRetryOnFailure`). If someone switches them on, a failed
commit would re-run the *whole handler*, outside call included. Against a service with a small monthly
request allowance, that's requests spent twice.

### Work that only goes into a queue

If a command only puts work into a queue for later (the way app logs are queued), `TransactionBehavior`
still wraps it. The transaction is opened and committed with nothing in it. That's harmless, just a
little wasted work.

### Queries that return pages

```csharp
public record GetPriceSourcesQuery(
    PaginationQuery? Pagination,
    PriceSourceFilterDto? Filter
) : IQuery<PageResult<PriceSourceDto>>;
```

In the handler, use `PaginationHelper.GetEffectivePagination()` to get the page number and size, and
`PaginationHelper.CreatePageResult()` to build the result. Both are in
`src/Aurum.App.SharedKernel/Common/Pagination.cs`.

### Exception: a controller calling an outside-system client directly

> **CLAUDE.md overrides this section.** CLAUDE.md says a controller that goes around MediatR to reach
> Infrastructure code breaks the layer rules. This section describes an exception that allows exactly
> that. It came from the application these guides were first written for. Follow CLAUDE.md until the
> two are reconciled.

As originally written, a controller could use an Infrastructure client directly when all of these
were true:

- The client is in an `Aurum.App.Infrastructure.*` project and wraps an outside system (an AI model,
  file storage, email, text messages, another company's API).
- The controller action only passes the request straight through to that client, with no business rules
  that would belong in a handler.
- The client is still received through the constructor (not looked up by hand), and every other
  controller rule is met: the four attributes, `IAppLogService<T>` for success and error, a
  `CancellationToken`, and `try`/`catch` returning safe error messages.

It stops applying as soon as logic gathers around the call: checking beyond basic shape, saving to the
database, coordinating several kinds of entity, or business rules. Then move it into a command and
handler, and send it through `IMediator` like any other feature.

---

## Rules that block a merge

These are checked in code review. Breaking one blocks the merge.

### 1. Handlers never return entities

Handlers return **DTOs only**, never Entity Framework entities. An entity shows the caller how the
database is laid out, and ties the controller to it.

```csharp
// ❌ BAD: returns an entity
public record SetPriceSourceEnabledCommand(...) : ICommand<PriceSource>;

// ✅ GOOD: returns a DTO
public record SetPriceSourceEnabledCommand(...) : ICommand<PriceSourceDto>;
```

Copy the entity into a DTO inside the handler before returning.

### 2. Every async controller action takes a CancellationToken

Every `async Task<IActionResult>` action must have `CancellationToken cancellationToken = default`, and
pass it on to `_mediator.Send()`, `_appLogService`, and every other async call. That way, if the caller
gives up, the work stops too.

```csharp
// ❌ BAD: no CancellationToken
public async Task<IActionResult> GetByCode(string sourceCode) { ... }

// ✅ GOOD
public async Task<IActionResult> GetByCode(string sourceCode, CancellationToken cancellationToken = default) { ... }
```

### 3. Get dependencies through the constructor, never by looking them up

Never fetch a dependency with `HttpContext.RequestServices.GetRequiredService<T>()`. Always take it in
the constructor. Looking it up hides what the class needs, and stops tests from swapping in fakes.

```csharp
// ❌ BAD: looked up by hand
var feed = HttpContext.RequestServices.GetRequiredService<IPriceFeed>();

// ✅ GOOD: taken in the constructor
public PriceSourcesController(IMediator mediator, IAppLogService<PriceSourcesController> appLogService) { ... }
```

### 4. Every controller action logs success and error

Use `IAppLogService<T>`, never `ILogger<T>`. Every action needs:

- **Success:** `_appLogService.LogAsync(LogType.ActionLog, "ActionName", details, statusCode)`
- **Error:** `_appLogService.LogErrorAsync("ActionNameError", exception, statusCode)`

That includes actions that return files (PDFs, downloads): log before returning `File()`.

**Exception:** actions that change security settings log failures only (see CLAUDE.md).

### 5. Every answer uses `ApiResponse<T>`

Never return an anonymous object (`new { ... }`) or an entity. Always use
`ApiResponse<T>.CreateSuccess(dto)` or `ApiResponse<T>.CreateError(message, code)`.

### 6. Names match the interface

If a record implements `ICommand<T>`, its name ends in `Command`. If it implements `IQuery<T>`, its
name ends in `Query`. A check that doesn't change anything is a **query**, even if it's called with
`POST`.

```csharp
// ❌ BAD: called a Command, but it's a query
public record CheckPriceSourceHealthCommand(...) : IQuery<PriceSourceHealthDto?>;

// ✅ GOOD: name matches the interface
public record CheckPriceSourceHealthQuery(...) : IQuery<PriceSourceHealthDto?>;
```

Put queries in `{Feature}/Queries/` and their handlers in `{Feature}/Handlers/Queries/`.

### 7. No anonymous objects in results

Commands and queries return **named DTOs**, never `object`, `List<object>` or anonymous objects.
Anonymous objects have no fixed shape the compiler can check, so they break silently when a field is
renamed.

```csharp
// ❌ BAD: anonymous object, nothing checks its shape
public record GetPriceSourcesQuery(...) : IQuery<List<object>>;
// handler returns: new { sourceCode = ..., lastSuccessAt = ... }

// ✅ GOOD: a named DTO
public record GetPriceSourcesQuery(...) : IQuery<List<PriceSourceDto>>;
```

### 8. Use constants for fixed strings and IDs

Never write literal strings like `"goldapi.io"` or `"XAUUSD"` in handler code. Use the constants:

```csharp
// ❌ BAD: typed-in strings
tick.SourceCode = "goldapi.io";
tick.Symbol = "XAUUSD";

// ✅ GOOD: named constants
tick.SourceCode = GoldApiIoSource.SourceCode;
tick.Symbol = SupportedSymbol.Gold;
```

If there's no constants class for a feature yet, create one:
`Aurum.App.Application/{Feature}/{Feature}Constants.cs`.

### 9. Command handlers use repositories, not `AurumDbContext`

Command handlers take **repository interfaces** (`IPriceSourceRepository`), not `AurumDbContext`. If
the repository is missing something (such as a bulk insert), **add it to the interface and the class**
instead of reaching for the database context.

```csharp
// ❌ BAD: database context in a command handler
public class SetPriceSourceEnabledCommandHandler(AurumDbContext context) { ... }

// ✅ GOOD: repository interface
public class SetPriceSourceEnabledCommandHandler(IPriceSourceRepository repository) { ... }
```

Query handlers may use `AurumDbContext` for reads. `docs/best-practices-api.md` also allows one narrow
exception for command handlers that read a *different* kind of entity or do bulk updates.

### 10. Say so when a command holds something that can't be turned into JSON

If a command record holds something that can't be turned into JSON, such as a `Stream`, add a comment
explaining why. Pipeline steps like logging may try to turn the request into JSON.

```csharp
/// <summary>
/// Note: Csv is a Stream on purpose, so a large price history file is read as it arrives
/// instead of being loaded into memory first. It can't be turned into JSON.
/// </summary>
public record ImportPriceHistoryCommand(
    Stream Csv,
    string FileName,
    int UserId
) : ICommand<ImportResultDto>;
```

(A made-up example: there's no import feature.)

### 11. Check who the user is before changing anything

Every endpoint that changes data (`POST`, `PUT`, `PATCH`, `DELETE`) and needs the logged-in user's ID
must check it before sending the command. Use a `ValidateUserId` helper that answers "not logged in"
(HTTP 401) if the ID is missing. The helper doesn't exist yet. This is its planned shape:

```csharp
private IActionResult? ValidateUserId(out int userId)
{
    userId = GetUserId();
    // 0 means the login token had no user ID in it
    return userId == 0
        ? Unauthorized(ApiResponse<object>.CreateError("Unable to determine user identity", 401))
        : null;
}

// In an action:
var authError = ValidateUserId(out var userId);
if (authError != null) return authError;
```

### 12. Every new DTO gets a matching TypeScript type in the app

When you add a DTO, add a TypeScript `interface` for it in the app's matching service file. Add a
comment naming the C# class, and noting that field names arrive in camelCase (`sourceCode`, not
`SourceCode`), because that's what ASP.NET Core sends by default.

```typescript
/**
 * Matches Aurum.App.Application.PriceSources.DTOs.PriceSourceDto
 * Field names are camelCase (ASP.NET Core default).
 */
export interface PriceSourceDto {
  sourceCode: string;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
}
```

### 13. Every command has a validator

Every command that changes data (`ICommand<T>`) needs a validator in
`Aurum.App.Application/{Feature}/Validators/`. They're found automatically. At a minimum, check
required fields and who the user is.

```csharp
// Aurum.App.Application/PriceSources/Validators/SetPriceSourcePriorityCommandValidator.cs
public class SetPriceSourcePriorityCommandValidator : AbstractValidator<SetPriceSourcePriorityCommand>
{
    public SetPriceSourcePriorityCommandValidator()
    {
        RuleFor(x => x.SourceCode).NotEmpty().WithMessage("Source code is required");
        RuleFor(x => x.Priority).GreaterThan(0).WithMessage("Priority must be 1 or more");
        RuleFor(x => x.UserId).GreaterThan(0).WithMessage("Valid user ID is required");
    }
}
```

---

## Tests (required)

**Every feature needs tests for its controller, its handlers and its validators.** All three.

**Where the test files go:**

- `Controllers/{ControllerName}Tests.cs`
- `Handlers/{Feature}CommandHandlerTests.cs`
- `Handlers/{Feature}QueryHandlerTests.cs`
- `Validators/{Feature}CommandValidatorTests.cs`

The examples below use Moq, a library for making fake versions of interfaces.

### Controller tests

Fake `IMediator`. Never fake services directly.

```csharp
private readonly Mock<IMediator> _mockMediator = new();
private readonly Mock<IAppLogService<PriceSourcesController>> _mockLogService = new();

[Fact]
public async Task GetByCode_WithKnownCode_ReturnsOk()
{
    var dto = new PriceSourceDto("goldapi.io", LastSuccessAt: null, LastFailureAt: null);
    _mockMediator
        .Setup(m => m.Send(It.IsAny<GetPriceSourceByCodeQuery>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(dto);

    var controller = new PriceSourcesController(_mockMediator.Object, _mockLogService.Object);
    var result = await controller.GetByCode("goldapi.io", CancellationToken.None);

    // 200, with the DTO inside the ApiResponse wrapper
    var okResult = Assert.IsType<OkObjectResult>(result);
    var body = Assert.IsType<ApiResponse<PriceSourceDto>>(okResult.Value);
    Assert.Equal(dto, body.Data);
}
```

### Handler tests (required)

**Command handlers:** fake `IUnitOfWork` and the repository. Test success (the change is made), not
found (returns false or empty), and check whether saving was called.

**Query handlers:** fake the repository, or use Entity Framework's in-memory database. Test that data
comes back, that an empty result comes back empty, and that a missing ID returns nothing.

```csharp
[Fact]
public async Task Handle_WithExistingSource_ReturnsTrue()
{
    var entity = new PriceSource { Code = "goldapi.io" };
    _mockRepository.Setup(r => r.GetByCodeAsync("goldapi.io", It.IsAny<CancellationToken>())).ReturnsAsync(entity);

    var result = await _handler.Handle(new DeletePriceSourceCommand("goldapi.io"), CancellationToken.None);

    Assert.True(result);
    _mockRepository.Verify(r => r.Remove(entity), Times.Once);
    _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task Handle_WithUnknownSource_ReturnsFalse()
{
    _mockRepository.Setup(r => r.GetByCodeAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((PriceSource?)null);

    var result = await _handler.Handle(new DeletePriceSourceCommand("nope"), CancellationToken.None);

    Assert.False(result);
    _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
}
```

### Validator tests (required)

Create the validator directly, no fakes needed. Test that good input passes, that each required field
fails when empty, and the edge values (exactly the maximum length, and one over).

```csharp
[Fact]
public async Task Validate_WithValidData_IsValid()
{
    var validator = new SetPriceSourceEnabledCommandValidator();
    var command = new SetPriceSourceEnabledCommand("goldapi.io", Enabled: true);

    var result = await validator.ValidateAsync(command);
    Assert.True(result.IsValid);
}

[Fact]
public async Task Validate_EmptySourceCode_HasError()
{
    var validator = new SetPriceSourceEnabledCommandValidator();
    var command = new SetPriceSourceEnabledCommand("", Enabled: true);

    var result = await validator.ValidateAsync(command);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, e => e.PropertyName.Contains("SourceCode"));
}

[Fact]
public async Task Validate_SourceCodeOneOverMaxLength_HasError()
{
    var validator = new SetPriceSourceEnabledCommandValidator();
    var command = new SetPriceSourceEnabledCommand(new string('a', 51), Enabled: true);

    var result = await validator.ValidateAsync(command);
    Assert.False(result.IsValid);
}
```

---

## Files to look at

| File | What it is |
|---|---|
| `src/Aurum.App.Application/Common/CQRS/ICommand.cs` | The interface that marks a command |
| `src/Aurum.App.Application/Common/CQRS/IQuery.cs` | The interface that marks a query |
| `src/Aurum.App.Application/Common/Behaviors/LoggingBehavior.cs` | The logging step |
| `src/Aurum.App.Application/Common/Behaviors/ValidationBehavior.cs` | The validation step |
| `src/Aurum.App.Application/Common/Behaviors/TransactionBehavior.cs` | The transaction step |
| `src/Aurum.App.Infrastructure.Data/Repositories/IUnitOfWork.cs` | Saving, and running work in one transaction |
| `src/Aurum.App.SharedKernel/Common/Pagination.cs` | `PageResult<T>` and the paging helpers |
| `src/Aurum.App.Application/AppLogs/` | App logging. It's not a command/query feature, and there are no converted controllers yet. |
| `src/Aurum.Api/Program.cs` | Where MediatR and FluentValidation are set up |
