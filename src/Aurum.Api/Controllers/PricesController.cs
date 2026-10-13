using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Aurum.Api.Controllers;

/// <remarks>No [Authorize]: Phase 1 registers no login method, so it would refuse every call (D-20).</remarks>
[ApiController]
[Route("api/v1/price")]
[Produces("application/json")]
public sealed class PricesController(IMediator mediator, IAppLogService<PricesController> appLogService)
    : ControllerBase
{
    public const string NoPriceMessage =
        "No price has been received since the app started and none is stored yet. " +
        "The poller fills this on its next successful poll.";

    public const string UnexpectedErrorMessage = "An unexpected error occurred.";

    /// <summary>The held price. 503 when the app has none yet; never calls a price service.</summary>
    [HttpGet("live")]
    public async Task<ActionResult<ApiResponse<LivePriceDto>>> GetLive(
        [FromQuery] string? symbol, CancellationToken ct)
    {
        const string action = nameof(GetLive);
        symbol ??= SupportedSymbol.Gold;

        try
        {
            var price = await mediator.Send(new GetLivePriceQuery(symbol), ct);

            var response = price is null
                ? ApiResponse<LivePriceDto>.CreateError(NoPriceMessage, StatusCodes.Status503ServiceUnavailable)
                : ApiResponse<LivePriceDto>.CreateSuccess(price);

            await appLogService.LogAsync(LogType.ActionLog, action, $"symbol={symbol}", response.StatusCode);
            return StatusCode(response.StatusCode, response);
        }
        catch (ValidationException ex)
        {
            await appLogService.LogAsync(LogType.ActionLog, action, ex.Message, StatusCodes.Status400BadRequest);
            return BadRequest(ApiResponse<LivePriceDto>.CreateValidationError(ex.ToErrors()));
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync(action, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<LivePriceDto>.CreateError(UnexpectedErrorMessage));
        }
    }
}
