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

    private static (int? StatusCode, ApiResponse<T> Body) Unwrap<T>(ActionResult<ApiResponse<T>> result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        return (objectResult.StatusCode, Assert.IsType<ApiResponse<T>>(objectResult.Value));
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
        Assert.Equal(ErrorMessages.NoPriceMessage, body.Message);
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
        Assert.Equal(ErrorMessages.UnexpectedErrorMessage, body.Message); // never the exception's own text
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(PricesController.GetLive), failure, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLive_WhenCallerCancels_RethrowsWithoutLoggingAnError()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _mediator.Setup(m => m.Send(It.IsAny<GetLivePriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateController().GetLive(SupportedSymbol.Gold, cancelled.Token));

        _appLog.Verify(
            l => l.LogErrorAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── GET /api/v1/price/history ─────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset HistoryFrom = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly CursorPage<PriceTickDto> HistoryPage = new(
        [new PriceTickDto(HistoryFrom, HistoryFrom.AddSeconds(2), 2650.30m, null, null, "goldapi.io")],
        NextCursor: new Cursor(HistoryFrom, 41).Encode());

    [Fact]
    public async Task GetHistory_WithValidRange_Returns200WithThePageAndLogsSuccess()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceHistoryQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(HistoryPage);

        var (status, body) = Unwrap(await CreateController()
            .GetHistory(SupportedSymbol.Gold, HistoryFrom, null, 10, null, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(body.Success);
        Assert.Same(HistoryPage, body.Data);
        _appLog.Verify(
            l => l.LogAsync(LogType.ActionLog, nameof(PricesController.GetHistory), It.IsAny<string?>(), 200,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetHistory_WithEmptyRange_Returns200WithNoItems()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<PriceTickDto>([], NextCursor: null));

        var (status, body) = Unwrap(await CreateController()
            .GetHistory(SupportedSymbol.Gold, HistoryFrom, null, null, null, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data.Items);
        Assert.Null(body.Data.NextCursor);
    }

    [Fact]
    public async Task GetHistory_PassesEveryParameterThroughAndDefaultsSymbolToGold()
    {
        var to = HistoryFrom.AddHours(6);
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceHistoryQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(HistoryPage);

        await CreateController().GetHistory(null, HistoryFrom, to, 25, "abc", CancellationToken.None);

        _mediator.Verify(
            m => m.Send(new GetPriceHistoryQuery(SupportedSymbol.Gold, HistoryFrom, to, 25, "abc"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetHistory_WithFromOlderThanRetention_Returns400WithFieldErrors()
    {
        const string message = "The database keeps 30 days of prices, so 'from' must be on or after 2026-09-12T00:00:00.0000000+00:00.";
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException([new ValidationFailure("From", message)]));

        var (status, body) = Unwrap(await CreateController()
            .GetHistory(SupportedSymbol.Gold, HistoryFrom.AddDays(-40), null, null, null, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.NotNull(body.Errors);
        Assert.Equal([message], body.Errors["From"]);
    }

    [Fact]
    public async Task GetHistory_WhenHandlerThrows_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("boom");
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceHistoryQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var (status, body) = Unwrap(await CreateController()
            .GetHistory(SupportedSymbol.Gold, HistoryFrom, null, null, null, CancellationToken.None));

        Assert.Equal(500, status);
        Assert.Equal(ErrorMessages.UnexpectedErrorMessage, body.Message);
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(PricesController.GetHistory), failure, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── GET /api/v1/price/sources ─────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<PriceSourceStatusDto> Sources =
    [
        new("goldapi.io", "GoldAPI.io", 1, Enabled: true, 100, null, null, null,
            new CircuitStatusDto(false, 0, null),
            new QuotaStatusDto(40, 100, 0, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ProviderRejected: true)),
    ];

    [Fact]
    public async Task GetSources_Returns200WithTheListAndLogsSuccess()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceSourcesQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Sources);

        var (status, body) = Unwrap(await CreateController().GetSources(CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Same(Sources, body.Data);
        _appLog.Verify(
            l => l.LogAsync(LogType.ActionLog, nameof(PricesController.GetSources), It.IsAny<string?>(), 200,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSources_WithNoSources_Returns200WithAnEmptyList()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceSourcesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PriceSourceStatusDto>());

        var (status, body) = Unwrap(await CreateController().GetSources(CancellationToken.None));

        Assert.Equal(200, status);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetSources_WhenHandlerThrows_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("boom");
        _mediator.Setup(m => m.Send(It.IsAny<GetPriceSourcesQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var (status, body) = Unwrap(await CreateController().GetSources(CancellationToken.None));

        Assert.Equal(500, status);
        Assert.Equal(ErrorMessages.UnexpectedErrorMessage, body.Message);
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(PricesController.GetSources), failure, 500, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
