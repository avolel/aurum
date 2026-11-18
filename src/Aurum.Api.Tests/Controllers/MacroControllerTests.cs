using Aurum.Api.Controllers;
using Aurum.App.Application.Macro.DTOs;
using Aurum.App.Application.Macro.Queries;
using Aurum.App.Logging;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Aurum.Api.Tests.Controllers;

public class MacroControllerTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 13, 13, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<MacroFigureDto> Figures =
    [
        new("CPIAUCSL", "Consumer Price Index for All Urban Consumers", "Index 1982-1984=100", 331.2m,
            new DateOnly(2026, 9, 1), new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.Zero), AgeDays: 20),
    ];

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAppLogService<MacroController>> _appLog = new();

    private MacroController CreateController() =>
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
    public async Task GetSnapshot_WithFigures_Returns200AndLogsSuccess()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Figures);

        var (status, body) = Unwrap(await CreateController().GetSnapshot(AsOf, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(body.Success);
        Assert.Same(Figures, body.Data);
        _appLog.Verify(
            l => l.LogAsync(LogType.ActionLog, nameof(MacroController.GetSnapshot), It.IsAny<string?>(), 200,
                It.IsAny<long?>(), LogLevel.Information, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSnapshot_WithNoFigures_Returns200WithAnEmptyList()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MacroFigureDto>());

        var (status, body) = Unwrap(await CreateController().GetSnapshot(null, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetSnapshot_PassesAsOfThrough()
    {
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Figures);

        await CreateController().GetSnapshot(AsOf, CancellationToken.None);

        _mediator.Verify(m => m.Send(new GetMacroSnapshotQuery(AsOf), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSnapshot_WithAsOfInTheFuture_Returns400WithFieldErrors()
    {
        const string message = "'asOf' must not be in the future. Leave it out to mean now.";
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException([new ValidationFailure("AsOf", message)]));

        var (status, body) = Unwrap(await CreateController().GetSnapshot(AsOf.AddYears(1), CancellationToken.None));

        Assert.Equal(400, status);
        Assert.False(body.Success);
        Assert.NotNull(body.Errors);
        Assert.Equal([message], body.Errors["AsOf"]);
    }

    [Fact]
    public async Task GetSnapshot_WhenHandlerThrows_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("BOOM!!");
        // A live token, so forwarding it and swapping in None are distinguishable.
        using var request = new CancellationTokenSource();
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), request.Token)).ThrowsAsync(failure);

        var (status, body) = Unwrap(await CreateController().GetSnapshot(AsOf, request.Token));

        Assert.Equal(500, status);
        Assert.Equal(ErrorMessages.UnexpectedErrorMessage, body.Message);
        _appLog.Verify(
            l => l.LogErrorAsync(nameof(MacroController.GetSnapshot), failure, 500, null, LogType.ActionLog, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task GetSnapshot_WhenCallerCancels_RethrowsWithoutLoggingAnError()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _mediator.Setup(m => m.Send(It.IsAny<GetMacroSnapshotQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateController().GetSnapshot(AsOf, cancelled.Token));

        _appLog.Verify(
            l => l.LogErrorAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<LogType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
