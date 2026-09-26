# CQRS with MediatR — Developer Guide
 
## Overview
 
This project uses CQRS (Command Query Responsibility Segregation) with MediatR to separate read operations (queries) from write operations (commands). All new controller endpoints should follow this pattern.
 
### Pipeline Flow
 
```
Controller → IMediator.Send() → LoggingBehavior → ValidationBehavior → TransactionBehavior* → Handler
                                                                         (* commands only)
```
 
- **LoggingBehavior** — logs every request to the database via `IAppLogService`, warns on requests >500ms
- **ValidationBehavior** — runs FluentValidation validators, throws `ValidationException` on failure
- **TransactionBehavior** — wraps `ICommand<>` requests in `IUnitOfWork.ExecuteInTransactionAsync()`, skips queries
 
---
 
## Folder Structure
 
For a feature called `Widgets`:
 
```
Aurum.App.Application/
  Widgets/
    Commands/
      CreateWidgetCommand.cs
      UpdateWidgetCommand.cs
      DeleteWidgetCommand.cs
    Queries/
      GetWidgetsQuery.cs
      GetWidgetByIdQuery.cs
    Handlers/
      Commands/
        CreateWidgetCommandHandler.cs
        UpdateWidgetCommandHandler.cs
        DeleteWidgetCommandHandler.cs
      Queries/
        GetWidgetsQueryHandler.cs
        GetWidgetByIdQueryHandler.cs
    Validators/
      CreateWidgetCommandValidator.cs
      UpdateWidgetCommandValidator.cs
    DTOs/
      WidgetDto.cs           (existing)
      CreateWidgetDto.cs     (existing)
    Services/
      IWidgetService.cs      (existing — keep for cross-cutting use if needed)
```
 
---
 
## Step-by-Step: Converting a Controller Endpoint
 
### 1. Determine Command vs Query
 
| If the endpoint... | Use |
|---|---|
| Reads data (GET) | `IQuery<TResponse>` |
| Creates, updates, or deletes data (POST/PUT/DELETE) | `ICommand<TResponse>` |
 
Commands get wrapped in a DB transaction automatically. Queries do not.
 
### 2. Create the Request
 
**Query example** — `Queries/GetWidgetByIdQuery.cs`:
```csharp
using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Widgets.DTOs;
 
namespace Aurum.App.Application.Widgets.Queries;
 
public record GetWidgetByIdQuery(int Id) : IQuery<WidgetDto?>;
```
 
**Command example** — `Commands/CreateWidgetCommand.cs`:
```csharp
using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Widgets.DTOs;
 
namespace Aurum.App.Application.Widgets.Commands;
 
public record CreateWidgetCommand(CreateWidgetDto Data) : ICommand<WidgetDto>;
```
 
**Key rules:**
- Use `record` types — they're immutable and get value equality for free
- Query responses can be nullable (`WidgetDto?`) for single-item lookups
- Paginated queries return `PageResult<T>`
- Commands return the result type (`WidgetDto`, `bool`, etc.)
 
### 3. Create the Handler
 
**Query handler** — `Handlers/Queries/GetWidgetByIdQueryHandler.cs`:
```csharp
using Aurum.App.Application.Widgets.DTOs;
using Aurum.App.Application.Widgets.Queries;
using Aurum.App.Infrastructure.Data.Data;
using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
 
namespace Aurum.App.Application.Widgets.Handlers.Queries;
 
public class GetWidgetByIdQueryHandler : IRequestHandler<GetWidgetByIdQuery, WidgetDto?>
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;
 
    public GetWidgetByIdQueryHandler(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }
 
    public async Task<WidgetDto?> Handle(GetWidgetByIdQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.Widgets
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == request.Id, cancellationToken);
 
        return entity != null ? _mapper.Map<WidgetDto>(entity) : null;
    }
}
```
 
**Command handler** — `Handlers/Commands/CreateWidgetCommandHandler.cs`:
```csharp
using Aurum.App.Application.Widgets.Commands;
using Aurum.App.Application.Widgets.DTOs;
using Aurum.App.Infrastructure.Data.Data;
using AutoMapper;
using MediatR;
 
namespace Aurum.App.Application.Widgets.Handlers.Commands;
 
public class CreateWidgetCommandHandler : IRequestHandler<CreateWidgetCommand, WidgetDto>
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;
 
    public CreateWidgetCommandHandler(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }
 
    public async Task<WidgetDto> Handle(CreateWidgetCommand request, CancellationToken cancellationToken)
    {
        var entity = _mapper.Map<Widget>(request.Data);
 
        _context.Widgets.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
 
        return _mapper.Map<WidgetDto>(entity);
    }
}
```
 
**Handler rules:**
- Query handlers: inject `ApplicationDbContext` directly, always use `AsNoTracking()`
- Command handlers: inject `ApplicationDbContext` or repositories as needed
- Always pass `cancellationToken` to async calls
- Handlers contain the business logic — not the controller
- Never return raw entities — always map to DTOs
 
### 4. Add Validation (Commands Only)
 
`Validators/CreateWidgetCommandValidator.cs`:
```csharp
using Aurum.App.Application.Widgets.Commands;
using FluentValidation;
 
namespace Aurum.App.Application.Widgets.Validators;
 
public class CreateWidgetCommandValidator : AbstractValidator<CreateWidgetCommand>
{
    public CreateWidgetCommandValidator()
    {
        RuleFor(x => x.Data.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters");
 
        RuleFor(x => x.Data.Code)
            .NotEmpty().WithMessage("Code is required");
    }
}
```
 
Validators are auto-discovered by assembly scanning — no DI registration needed. The `ValidationBehavior` pipeline runs them automatically before the handler executes.
 
### 5. Update the Controller
 
**Before:**
```csharp
public class WidgetsController : ControllerBase
{
    private readonly IWidgetService _widgetService;
    private readonly IAppLogService<WidgetsController> _appLogService;
 
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _widgetService.GetByIdAsync(id, cancellationToken);
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
 
**After:**
```csharp
using MediatR;
 
public class WidgetsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IAppLogService<WidgetsController> _appLogService;
 
    public WidgetsController(IMediator mediator, IAppLogService<WidgetsController> appLogService)
    {
        _mediator = mediator;
        _appLogService = appLogService;
    }
 
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(new GetWidgetByIdQuery(id), cancellationToken);
            if (result == null) return NotFound(...);
            return Ok(result);
        }
        catch (Exception ex)
        {
            await _appLogService.LogErrorAsync(...);
            return StatusCode(500, ...);
        }
    }
 
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWidgetDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(new CreateWidgetCommand(dto), cancellationToken);
            return Ok(result);
        }
        catch (FluentValidation.ValidationException ex)
        {
            var errors = ex.Errors.Select(e => e.ErrorMessage).ToList();
            return BadRequest(new { errors });
        }
        catch (Exception ex)
        {
            await _appLogService.LogErrorAsync(...);
            return StatusCode(500, ...);
        }
    }
}
```
 
**Controller rules:**
- Inject `IMediator` as the primary dependency
- Keep `IAppLogService<TController>` only for error logging in catch blocks
- Catch `FluentValidation.ValidationException` on command endpoints and return 400
- Remove any manual input validation that is now handled by validators
- Controllers should be thin — no business logic, just dispatch and HTTP concerns
 
### 6. No DI Registration Needed
 
MediatR auto-discovers all handlers and FluentValidation auto-discovers all validators from the `Aurum.App.Application` assembly. Just create the files and they work.
 
---
 
## Special Cases
 
### External API Calls + DB Writes
 
When a command calls an external API and also writes to the DB, keep the API call outside the transaction:
 
```csharp
public async Task<ResultDto> Handle(CreateFromExternalCommand request, CancellationToken cancellationToken)
{
    // 1. Call external API (outside transaction — TransactionBehavior hasn't committed yet)
    var externalResult = await _externalService.CallAsync(request.Data, cancellationToken);
 
    // 2. Save to DB (this part is wrapped by TransactionBehavior)
    var entity = _mapper.Map<Entity>(externalResult);
    _context.Entities.Add(entity);
    await _context.SaveChangesAsync(cancellationToken);
 
    return _mapper.Map<ResultDto>(entity);
}
```
 
### Queue-Based Operations (No Transaction Needed)
 
If the command queues work for background processing (like AppLogs), the `TransactionBehavior` still wraps it but `ExecuteInTransactionAsync` is a no-op if no DB calls are made inside it.
 
### Paginated Queries
 
```csharp
public record GetWidgetsQuery(
    PaginationQuery? Pagination,
    WidgetFilterDto? Filter
) : IQuery<PageResult<WidgetDto>>;
```
 
Use `PaginationHelper.GetEffectivePagination()` and `PaginationHelper.CreatePageResult()` in the handler — same as existing service code.
 
The controller still satisfies all other controller rules: `[ApiController]` + `[Authorize]` + `[Route("api/v1/...")]` + `[Produces]`, `IAppLogService<T>` for success/error logging, `CancellationToken` on the action, and try/catch with sanitized error responses.
 
When this exception applies:
 
- The dependency is an **infrastructure-layer client** (`Aurum.App.Infrastructure.*`) wrapping an external system (AI model, blob store, email, SMS, third-party API).
- The controller action is a **thin pass-through** to that client with no business logic to host in a handler.
- Constructor DI is still used (no service locator), and all other controller rules above are met.
 
When it does **not** apply: if logic accretes around the agent call (validation beyond shape checks, persistence, orchestration across multiple aggregates, business rules), migrate it to a CQRS command/handler and dispatch via `IMediator`
 like any other feature.
 
---
 
## Critical Rules (Violations Are Blockers)
 
These rules are enforced during code review. Violations block merge.
 
### 1. Handlers must never return entities
 
Handlers return **DTOs only** — never raw EF Core entities. Entities leak the database schema to the API consumer and couple the controller to the data model.
 
```csharp
// ❌ BAD — returns entity
public record UploadCommand(...) : ICommand<Referral>;
 
// ✅ GOOD — returns DTO
public record UploadCommand(...) : ICommand<UploadResultDto>;
```
 
Map entity → DTO inside the handler before returning.
 
### 2. Every async controller action must accept CancellationToken
 
Every `async Task<IActionResult>` method must include `CancellationToken cancellationToken = default` and pass it through to `_mediator.Send()`, `_appLogService`, and any other async calls. This ensures request cancellation propagates correctly.
 
```csharp
// ❌ BAD — no CancellationToken
public async Task<IActionResult> Upload(IFormFile file) { ... }
 
// ✅ GOOD
public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken = default) { ... }
```
 
### 3. No service locator — always use constructor DI
 
Never resolve dependencies via `HttpContext.RequestServices.GetRequiredService<T>()`. Always inject through the constructor. Service locator hides dependencies and prevents mocking in tests.
 
```csharp
// ❌ BAD — service locator
var service = HttpContext.RequestServices.GetRequiredService<ISomeService>();
 
// ✅ GOOD — constructor injection
public ReferralsController(IMediator mediator, ISomeService someService) { ... }
```
 
### 4. Every controller action must log on both success and error paths
 
Use `IAppLogService<T>` — never `ILogger<T>`. Every action needs:
 
- **Success:** `_appLogService.LogAsync(LogType.ActionLog, "ActionName", details, statusCode)`
- **Error:** `_appLogService.LogErrorAsync("ActionNameError", exception, statusCode)`
 
This includes binary-response endpoints (PDF views, file downloads) — log before returning `File()`.
 
### 5. All responses must use ApiResponse\<T\> wrapper
 
Never return anonymous types (`new { ... }`) or raw entities. Always use `ApiResponse<T>.CreateSuccess(dto)` or `ApiResponse<T>.CreateError(message, code)`.
 
### 6. Command/Query naming must match ICommand/IQuery interface
 
If a record implements `ICommand<T>`, name it `*Command`. If it implements `IQuery<T>`, name it `*Query`. A verify/check that does not mutate state is a
**query** even if called via POST.
 
```csharp
// ❌ BAD — named Command but implements IQuery
public record VerifyAddressCommand(...) : IQuery<Dictionary<string, object>?>;
 
// ✅ GOOD — naming matches interface
public record VerifyAddressQuery(...) : IQuery<Dictionary<string, object>?>;
```
 
Place queries in `{Feature}/Queries/` and their handlers in `{Feature}/Handlers/Queries/`.
 
### 7. No anonymous types in return values
 
Commands and queries must return **named DTOs** — never `object`, `List<object>`, or anonymous types. Anonymous types have no compile-time contract and break if property names change.
 
```csharp
// ❌ BAD — anonymous type, no type safety
public record UploadCommand(...) : ICommand<List<object>>;
// handler returns: new { referralId = ..., blobName = ... }
 
// ✅ GOOD — typed DTO
public record UploadCommand(...) : ICommand<List<UploadedDocumentResultDto>>;
```
 
### 8. Use constants for status strings and entry types
 
Never embed literal strings like `"New"`, `"PDF"`, `"Manual"` in handler or service code. Use shared constants:
 
```csharp
// ❌ BAD — magic strings
referral.FileStatus = "New";
referral.ReferralEntryType = "PDF";
 
// ✅ GOOD — constants in App.Application/Referrals/ReferralConstants.cs
referral.FileStatus = ReferralFileStatus.New;
referral.ReferralEntryType = ReferralEntryType.Pdf;
```
 
### 9. Command handlers must use repository interfaces, not ApplicationDbContext
 
Command handlers inject **repository interfaces** (`IReferralRepository`) — never `ApplicationDbContext` directly. If the repository lacks a needed method (e.g. batch insert),
**add it to the interface and implementation** rather than injecting DbContext.
 
```csharp
// ❌ BAD — DbContext in handler
public class UploadHandler(ApplicationDbContext context) { ... }
 
// ✅ GOOD — repository interface
public class UploadHandler(IReferralRepository repository) { ... }
```
 
Query handlers may inject `ApplicationDbContext` for read-only queries (accepted CQRS exception per `best-practices-api.md`).
 
### 10. Non-serializable types in commands must be documented
 
If a command record contains non-serializable types (e.g. `Stream`), add an XML doc comment explaining why. MediatR pipeline behaviors (logging, validation) may attempt to serialize the request.
 
```csharp
/// <summary>
/// Note: FileStreams contains non-serializable Stream objects — this is intentional
/// because file uploads must stream data directly.
/// </summary>
public record UploadDocumentsCommand(
    List<(Stream Content, string FileName)> FileStreams,
    int UserId
) : ICommand<List<UploadedDocumentResultDto>>;
```
 
### 11. Validate user identity on mutation endpoints
 
Every mutation endpoint (POST/PUT/PATCH/DELETE) that needs the authenticated user ID must validate it before dispatching. Use a `ValidateUserId` helper that returns 401 if the claim is missing.
 
```csharp
private IActionResult? ValidateUserId(out int userId)
{
    userId = GetUserId();
    return userId == 0
        ? Unauthorized(ApiResponse<object>.CreateError("Unable to determine user identity", 401))
        : null;
}
 
// Usage in action:
var authError = ValidateUserId(out var userId);
if (authError != null) return authError;
```
 
### 12. Every new DTO must have a frontend TypeScript counterpart
 
When creating a new backend DTO, add a corresponding TypeScript `interface` in the appropriate frontend service file. Include a JSDoc comment naming the C# class and noting camelCase serialization.
 
```typescript
/**
 * Mirrors App.Application.Referrals.DTOs.UploadedDocumentResultDto
 * JSON is camelCase (ASP.NET Core default).
 */
export interface UploadedDocumentResultDto {
  referralId: number;
  blobName: string;
  fileStatus: string;
  originalFileName: string;
}
```
 
### 13. Every command must have a FluentValidation validator
 
All mutating commands (`ICommand<T>`) must have a corresponding validator in `Aurum.App.Application/{Feature}/Validators/`. Validators are auto-discovered — no DI registration needed. At minimum, validate required fields and user identity.
 
```csharp
// App.Application/Referrals/Validators/UploadReferralFormCommandValidator.cs
public class UploadReferralFormCommandValidator : AbstractValidator<UploadReferralFormCommand>
{
    public UploadReferralFormCommandValidator()
    {
        RuleFor(x => x.PdfBytes).NotEmpty().WithMessage("PDF content is required");
        RuleFor(x => x.FileName).NotEmpty().Must(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        RuleFor(x => x.UserId).GreaterThan(0).WithMessage("Valid user ID is required");
    }
}
```
 
---
 
## Unit Testing (Required)
 
**Every CQRS feature must have tests for: controller, handlers, and validators.** All three are required.
 
**Test file location**:
 
- `Controllers/{ControllerName}Tests.cs`
- `Handlers/{Feature}CommandHandlerTests.cs`
- `Handlers/{Feature}QueryHandlerTests.cs`
- `Validators/{Feature}CommandValidatorTests.cs`
 
### Controller Tests
 
Mock `IMediator` — never mock services directly:
 
```csharp
private readonly Mock<IMediator> _mockMediator = new();
private readonly Mock<IAppLogService<WidgetsController>> _mockLogService = new();
 
[Fact]
public async Task GetById_WithValidId_ReturnsOk()
{
    var dto = new WidgetDto { Id = 1, Name = "Test" };
    _mockMediator
        .Setup(m => m.Send(It.IsAny<GetWidgetByIdQuery>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(dto);
 
    var controller = new WidgetsController(_mockMediator.Object, _mockLogService.Object);
    var result = await controller.GetById(1, CancellationToken.None);
 
    var okResult = Assert.IsType<OkObjectResult>(result);
    Assert.Equal(dto, okResult.Value);
}
```
 
### Handler Tests (Required)
 
**Command handlers**: Mock `IUnitOfWork` and `IRepository<T>`. Test success (entity saved), not-found (returns false/null), verify `SaveChangesAsync` called.
 
**Query handlers**: Mock `IRepository` or use in-memory EF Core. Test data returned, empty result, null for missing ID.
 
```csharp
[Fact]
public async Task Handle_WithExistingUser_ReturnsTrue()
{
    var entity = new User { UserId = 1 };
    _mockRepository.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
 
    var result = await _handler.Handle(new DeleteDbUserCommand(1), CancellationToken.None);
 
    Assert.True(result);
    _mockRepository.Verify(r => r.Remove(entity), Times.Once);
    _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
}
 
[Fact]
public async Task Handle_WithNonExistentUser_ReturnsFalse()
{
    _mockRepository.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
 
    var result = await _handler.Handle(new DeleteDbUserCommand(999), CancellationToken.None);
 
    Assert.False(result);
    _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
}
```
 
### Validator Tests (Required)
 
Instantiate directly — no mocking. Test valid input passes, each required field empty fails, boundary values (max length exact, max length + 1).
 
```csharp
[Fact]
public async Task Validate_WithValidData_IsValid()
{
    var validator = new CreateWidgetCommandValidator();
    var command = new CreateWidgetCommand(new CreateWidgetDto { Name = "Valid" });
 
    var result = await validator.ValidateAsync(command);
    Assert.True(result.IsValid);
}
 
[Fact]
public async Task Validate_EmptyName_HasError()
{
    var validator = new CreateWidgetCommandValidator();
    var command = new CreateWidgetCommand(new CreateWidgetDto { Name = "" });
 
    var result = await validator.ValidateAsync(command);
 
    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, e => e.PropertyName.Contains("Name"));
}
 
[Fact]
public async Task Validate_NameExceedsMaxLength_HasError()
{
    var validator = new CreateWidgetCommandValidator();
    var command = new CreateWidgetCommand(new CreateWidgetDto { Name = new string('A', 257) });
 
    var result = await validator.ValidateAsync(command);
    Assert.False(result.IsValid);
}
```
 
---
 
## Reference Files
 
| File | Purpose |
|---|---|
| `Aurum.App.Application/Common/CQRS/ICommand.cs` | Command marker interface |
| `Aurum.App.Application/Common/CQRS/IQuery.cs` | Query marker interface |
| `Aurum.App.Application/Common/Behaviors/LoggingBehavior.cs` | DB logging pipeline |
| `Aurum.App.Application/Common/Behaviors/ValidationBehavior.cs` | FluentValidation pipeline |
| `Aurum.App.Application/Common/Behaviors/TransactionBehavior.cs` | Transaction wrapping pipeline |
| `Aurum.App.Application/AppLogs/` | Reference implementation (first controller converted) |
| `Aurum.App.Api/Program.cs` | MediatR + FluentValidation DI registration |
 
