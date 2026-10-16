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
[Route("api/v1/price")]
[Produces("application/json")]
public sealed class PricesController(IMediator mediator, IAppLogService<PricesController> appLogService)
    : ControllerBase
{
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
                ? ApiResponse<LivePriceDto>.CreateError(ErrorMessages.NoPriceMessage, StatusCodes.Status503ServiceUnavailable)
                : ApiResponse<LivePriceDto>.CreateSuccess(price);

            await appLogService.LogAsync(LogType.ActionLog, action, $"symbol={symbol}", response.StatusCode);
            return StatusCode(response.StatusCode, response);
        }
        catch (ValidationException ex)
        {
            await appLogService.LogAsync(LogType.ActionLog, action, ex.Message, StatusCodes.Status400BadRequest);
            return BadRequest(ApiResponse<LivePriceDto>.CreateValidationError(ex.ToErrors()));
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
                ApiResponse<LivePriceDto>.CreateError(ErrorMessages.UnexpectedErrorMessage));
        }
    }

    /// <summary>Stored ticks in <c>[from, to)</c>, oldest first. 400 when <c>from</c> is older than the database keeps.</summary>
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<CursorPage<PriceTickDto>>>> GetHistory(
        [FromQuery] string? symbol,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken ct)
    {
        const string action = nameof(GetHistory);
        symbol ??= SupportedSymbol.Gold;

        try
        {
            var page = await mediator.Send(new GetPriceHistoryQuery(symbol, from, to, limit, cursor), ct);

            await appLogService.LogAsync(
                LogType.ActionLog, action, $"symbol={symbol} from={from:O} to={to:O} rows={page.Items.Count}",
                StatusCodes.Status200OK);
            return Ok(ApiResponse<CursorPage<PriceTickDto>>.CreateSuccess(page));
        }
        catch (ValidationException ex)
        {
            await appLogService.LogAsync(LogType.ActionLog, action, ex.Message, StatusCodes.Status400BadRequest);
            return BadRequest(ApiResponse<CursorPage<PriceTickDto>>.CreateValidationError(ex.ToErrors()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync(action, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<CursorPage<PriceTickDto>>.CreateError(ErrorMessages.UnexpectedErrorMessage));
        }
    }

    /// <summary>Every configured price source: priority, health, circuit and quota. Never calls a source.</summary>
    [HttpGet("sources")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PriceSourceStatusDto>>>> GetSources(CancellationToken ct)
    {
        const string action = nameof(GetSources);

        try
        {
            var sources = await mediator.Send(new GetPriceSourcesQuery(), ct);

            await appLogService.LogAsync(LogType.ActionLog, action, $"sources={sources.Count}", StatusCodes.Status200OK);
            return Ok(ApiResponse<IReadOnlyList<PriceSourceStatusDto>>.CreateSuccess(sources));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await appLogService.LogErrorAsync(action, ex);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<IReadOnlyList<PriceSourceStatusDto>>.CreateError(ErrorMessages.UnexpectedErrorMessage));
        }
    }
}
