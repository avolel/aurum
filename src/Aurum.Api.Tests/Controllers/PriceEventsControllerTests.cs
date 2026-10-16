using Aurum.Api.Controllers;
using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Aurum.Api.Tests.Controllers;

public class PriceEventsControllerTests
{
    private static readonly DateTimeOffset DetectedAt = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly CursorPage<PriceEventDto> Page = new(
        [
            new PriceEventDto(
                SupportedSymbol.Gold, DetectedAt, "5m", DetectedAt.AddMinutes(-5), DetectedAt, PriceEventDirection.Up,
                2650.00m, 2657.00m, 7.00m, 0.264151m, 0.052830m, null, 2, "goldapi.io", "goldapi.io",
                IsCrossSource: false, "default", "|5m| >= 0.25%"),
        ],
        NextCursor: null);

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAppLogService<PriceEventsController>> _appLog = new();

    private PriceEventsController CreateController() =>
        new(_mediator.Object, _appLog.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static (int? StatusCode, ApiResponse<CursorPage<PriceEventDto>> Body) Unwrap(
        ActionResult<ApiResponse<CursorPage<PriceEventDto>>> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return (objectResult.StatusCode, Assert.IsType<ApiResponse<CursorPage<PriceEventDto>>>(objectResult.Value));
    }

    [Fact]
    public async Task GetEvents_WithEvents_Returns200AndLogsSuccess()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceEventsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Page);

        var (status, body) = Unwrap(await CreateController().GetEvents(null, null, null, null, null, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Same(Page, body.Data);
        _appLog.Verify(
            l => l.LogAsync(LogType.ActionLog, nameof(PriceEventsController.GetEvents), It.IsAny<string?>(), 200,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetEvents_WithNoEvents_Returns200WithNoItems()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceEventsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<PriceEventDto>([], NextCursor: null));

        var (status, body) = Unwrap(await CreateController().GetEvents(null, null, null, null, null, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data.Items);
    }

    [Fact]
    public async Task GetEvents_PassesEveryParameterThroughAndDefaultsSymbolToGold()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceEventsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Page);

        await CreateController().GetEvents(null, "5m", 0.3m, 25, "abc", CancellationToken.None);

        _mediator.Verify(
            m => m.Send(new GetPriceEventsQuery(SupportedSymbol.Gold, "5m", 0.3m, 25, "abc"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetEvents_WithUnknownWindow_Returns400WithFieldErrors()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceEventsQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException([new ValidationFailure("Window", "'window' must be one of: 1m, 5m.")]));

        var (status, body) = Unwrap(await CreateController().GetEvents(null, "30m", null, null, null, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.NotNull(body.Errors);
        Assert.Equal(["'window' must be one of: 1m, 5m."], body.Errors["Window"]);
    }

    [Fact]
    public async Task GetEvents_WhenHandlerThrows_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("boom");
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceEventsQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var (status, body) = Unwrap(await CreateController().GetEvents(null, null, null, null, null, CancellationToken.None));

        Assert.Equal(500, status);
        Assert.Equal(ErrorMessages.UnexpectedErrorMessage, body.Message);
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(PriceEventsController.GetEvents), failure, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
