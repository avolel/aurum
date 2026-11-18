using Aurum.App.Application.Macro.DTOs;
using Aurum.App.Application.Macro.Queries;
using Aurum.App.Application.Validation;
using Aurum.App.Logging;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Aurum.Api.Controllers;

/// <remarks>No [Authorize]: Phase 1 registers no login method, so it would refuse every call (D-20).</remarks>
[ApiController]
[Route("api/v1/macro")]
[Produces("application/json")]
public sealed class MacroController(IMediator mediator, IAppLogService<MacroController> appLogService)
    : ControllerBase
{
    /// <summary>The newest stored figure of each series known at <c>asOf</c> (default now). Never calls FRED or BLS.</summary>
    [HttpGet("snapshot")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MacroFigureDto>>>> GetSnapshot(
        [FromQuery] DateTimeOffset? asOf, CancellationToken ct = default)
    {
        const string action = nameof(GetSnapshot);

        try
        {
            var figures = await mediator.Send(new GetMacroSnapshotQuery(asOf), ct);

            await appLogService.LogAsync(
                LogType.ActionLog, action, $"asOf={asOf:O} figures={figures.Count}", StatusCodes.Status200OK, ct: ct);
            return Ok(ApiResponse<IReadOnlyList<MacroFigureDto>>.CreateSuccess(figures));
        }
        catch (ValidationException ex)
        {
            await appLogService.LogAsync(LogType.ActionLog, action, ex.Message, StatusCodes.Status400BadRequest, ct: ct);
            return BadRequest(ApiResponse<IReadOnlyList<MacroFigureDto>>.CreateValidationError(ex.ToErrors()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync(action, ex, ct: CancellationToken.None);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<IReadOnlyList<MacroFigureDto>>.CreateError(ErrorMessages.UnexpectedErrorMessage));
        }
    }
}
