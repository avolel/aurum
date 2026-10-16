using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Application.Validation;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Aurum.Api.Controllers;

/// <remarks>No [Authorize]: Phase 1 registers no login method, so it would refuse every call (D-20).</remarks>
[ApiController]
[Route("api/v1/events")]
[Produces("application/json")]
public sealed class PriceEventsController(IMediator mediator, IAppLogService<PriceEventsController> appLogService)
    : ControllerBase
{
    /// <summary>Saved price-move events, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<CursorPage<PriceEventDto>>>> GetEvents(
        [FromQuery] string? symbol,
        [FromQuery] string? window,
        [FromQuery] decimal? minPercent,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken ct)
    {
        const string action = nameof(GetEvents);
        symbol ??= SupportedSymbol.Gold;

        try
        {
            var page = await mediator.Send(new GetPriceEventsQuery(symbol, window, minPercent, limit, cursor), ct);

            await appLogService.LogAsync(
                LogType.ActionLog, action, $"symbol={symbol} window={window} rows={page.Items.Count}",
                StatusCodes.Status200OK);
            return Ok(ApiResponse<CursorPage<PriceEventDto>>.CreateSuccess(page));
        }
        catch (ValidationException ex)
        {
            await appLogService.LogAsync(LogType.ActionLog, action, ex.Message, StatusCodes.Status400BadRequest);
            return BadRequest(ApiResponse<CursorPage<PriceEventDto>>.CreateValidationError(ex.ToErrors()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up; nobody reads a reply, and it is not a server fault.
            throw;
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync(action, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<CursorPage<PriceEventDto>>.CreateError(ErrorMessages.UnexpectedErrorMessage));
        }
    }
}
