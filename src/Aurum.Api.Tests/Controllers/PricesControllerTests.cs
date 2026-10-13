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

public class PricesControllerTests
{
    private static readonly LivePriceDto Gold = new(
        SupportedSymbol.Gold, 2650.30m, 2650.10m, 2650.50m,
        new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 12, 9, 30, 2, TimeSpan.Zero),
        "goldapi.io", IsFallback: false, AttemptedSources: ["goldapi.io"], AgeSeconds: 90, IsStale: false);

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAppLogService<PricesController>> _appLog = new();

    private PricesController CreateController() =>
        new(_mediator.Object, _appLog.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static (int? StatusCode, ApiResponse<LivePriceDto> Body) Unwrap(ActionResult<ApiResponse<LivePriceDto>> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return (objectResult.StatusCode, Assert.IsType<ApiResponse<LivePriceDto>>(objectResult.Value));
    }

    [Fact]
    public async Task GetLive_WithHeldPrice_Returns200AndLogsSuccess()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Gold);

        var (status, body) = Unwrap(await CreateController().GetLive(SupportedSymbol.Gold, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(body.Success);
        Assert.Same(Gold, body.Data);
        _appLog.Verify(
            l => l.LogAsync(LogType.ActionLog, nameof(PricesController.GetLive), It.IsAny<string?>(), 200,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLive_WithNoSymbol_AsksForGold()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Gold);

        await CreateController().GetLive(symbol: null, CancellationToken.None);

        _mediator.Verify(
            m => m.Send(new GetLivePriceQuery(SupportedSymbol.Gold), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLive_WithNoHeldPrice_Returns503WithReason()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LivePriceDto?)null);

        var (status, body) = Unwrap(await CreateController().GetLive(SupportedSymbol.Gold, CancellationToken.None));

        Assert.Equal(503, status);
        Assert.False(body.Success);
        Assert.Equal(503, body.StatusCode);
        Assert.Equal(PricesController.NoPriceMessage, body.Message);
    }

    [Fact]
    public async Task GetLive_WithUnsupportedSymbol_Returns400WithFieldErrors()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException([new ValidationFailure("Symbol", "Symbol must be one of: XAUUSD, XAGUSD.")]));

        var (status, body) = Unwrap(await CreateController().GetLive("XPTUSD", CancellationToken.None));

        Assert.Equal(400, status);
        Assert.False(body.Success);
        Assert.NotNull(body.Errors);
        Assert.Equal(["Symbol must be one of: XAUUSD, XAGUSD."], body.Errors["Symbol"]);
    }

    [Fact]
    public async Task GetLive_WhenHandlerThrows_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("boom");
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var (status, body) = Unwrap(await CreateController().GetLive(SupportedSymbol.Gold, CancellationToken.None));

        Assert.Equal(500, status);
        Assert.Equal(PricesController.UnexpectedErrorMessage, body.Message); // never the exception's own text
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(PricesController.GetLive), failure, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
