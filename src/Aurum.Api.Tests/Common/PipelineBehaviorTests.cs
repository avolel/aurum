using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Common.Behaviors;
using Aurum.App.Application.Common.CQRS;
using Aurum.App.Infrastructure.Data.Repositories;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Aurum.Api.Tests.Common;

public sealed record TestCommand(string Name) : ICommand<int>;
public sealed record TestUnitCommand : ICommand;
public sealed record TestQuery(string Name) : IQuery<int>;

/// <summary>
/// The three MediatR pipeline steps, each on its own, with no container.
/// </summary>
public class PipelineBehaviorTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>A handler stand-in that records whether the pipeline reached it.</summary>
    private sealed class Next<T>(T result)
    {
        public int Calls { get; private set; }

        public Task<T> Invoke(CancellationToken _ = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    // --- LoggingBehavior ---

    private static LoggingBehavior<TestQuery, int> Logging(Mock<IAppLogService<TestQuery>> appLog) =>
        new(appLog.Object, NullLogger<LoggingBehavior<TestQuery, int>>.Instance);

    [Fact]
    public async Task LoggingBehavior_OnSuccess_LogsActionWith200()
    {
        var appLog = new Mock<IAppLogService<TestQuery>>();
        var next = new Next<int>(7);

        var result = await Logging(appLog).Handle(new TestQuery("gold"), next.Invoke, Ct);

        Assert.Equal(7, result);
        appLog.Verify(l => l.LogAsync(
            LogType.ActionLog, nameof(TestQuery), null, 200, It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LoggingBehavior_OnValidationException_LogsBadRequest()
    {
        var appLog = new Mock<IAppLogService<TestQuery>>();
        var invalid = new ValidationException("Name is required.");

        var thrown = await Assert.ThrowsAsync<ValidationException>(() =>
            Logging(appLog).Handle(new TestQuery(""), _ => throw invalid, Ct));

        Assert.Same(invalid, thrown);
        appLog.Verify(l => l.LogErrorAsync(nameof(TestQuery), invalid, 400, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoggingBehavior_OnOtherException_LogsServerErrorAndRethrows()
    {
        var appLog = new Mock<IAppLogService<TestQuery>>();
        var failure = new InvalidOperationException("database is down");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Logging(appLog).Handle(new TestQuery("gold"), _ => throw failure, Ct));

        // Same instance: rethrown, not wrapped, so the controller's catch sees the real type.
        Assert.Same(failure, thrown);
        appLog.Verify(l => l.LogErrorAsync(nameof(TestQuery), failure, 500, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- ValidationBehavior ---

    private static InlineValidator<TestCommand> NameRequired()
    {
        var validator = new InlineValidator<TestCommand>();
        validator.RuleFor(c => c.Name).NotEmpty();
        return validator;
    }

    private static InlineValidator<TestCommand> NameShort()
    {
        var validator = new InlineValidator<TestCommand>();
        validator.RuleFor(c => c.Name).MaximumLength(3);
        return validator;
    }

    [Fact]
    public async Task ValidationBehavior_WithNoValidators_CallsNext()
    {
        var behavior = new ValidationBehavior<TestCommand, int>([]);
        var next = new Next<int>(7);

        var result = await behavior.Handle(new TestCommand(""), next.Invoke, Ct);

        Assert.Equal(7, result);
        Assert.Equal(1, next.Calls);
    }

    [Fact]
    public async Task ValidationBehavior_WithValidRequest_CallsNext()
    {
        var behavior = new ValidationBehavior<TestCommand, int>([NameRequired(), NameShort()]);
        var next = new Next<int>(7);

        await behavior.Handle(new TestCommand("abc"), next.Invoke, Ct);

        Assert.Equal(1, next.Calls);
    }

    [Fact]
    public async Task ValidationBehavior_WithFailures_ThrowsOneValidationExceptionWithAll()
    {
        // Two validators, each failing on its own: both failures must arrive in one exception.
        var alwaysFails = new InlineValidator<TestCommand>();
        alwaysFails.RuleFor(c => c.Name).Must(_ => false).WithMessage("second validator");
        var behavior = new ValidationBehavior<TestCommand, int>([NameRequired(), alwaysFails]);
        var next = new Next<int>(7);

        var thrown = await Assert.ThrowsAsync<ValidationException>(() =>
            behavior.Handle(new TestCommand(""), next.Invoke, Ct));

        Assert.Equal(2, thrown.Errors.Count());
        Assert.Contains(thrown.Errors, e => e.ErrorMessage == "second validator");
        Assert.Equal(0, next.Calls);
    }

    // --- TransactionBehavior ---

    /// <summary>
    /// A unit of work that runs the work it is given, as the real one does. A mock that only
    /// returned a value would never call the handler, so "inside the transaction" would go untested.
    /// </summary>
    private static Mock<IUnitOfWork> RunningUnitOfWork<T>()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<T>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<T>> work, CancellationToken ct) => work(ct));
        return unitOfWork;
    }

    [Fact]
    public async Task TransactionBehavior_ForAQuery_DoesNotOpenATransaction()
    {
        var unitOfWork = RunningUnitOfWork<int>();
        var next = new Next<int>(7);

        var result = await new TransactionBehavior<TestQuery, int>(unitOfWork.Object)
            .Handle(new TestQuery("gold"), next.Invoke, Ct);

        Assert.Equal(7, result);
        Assert.Equal(1, next.Calls);
        unitOfWork.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task<int>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransactionBehavior_ForACommand_RunsInsideExecuteInTransactionAsync()
    {
        var unitOfWork = RunningUnitOfWork<int>();
        var next = new Next<int>(7);

        var result = await new TransactionBehavior<TestCommand, int>(unitOfWork.Object)
            .Handle(new TestCommand("gold"), next.Invoke, Ct);

        Assert.Equal(7, result);
        Assert.Equal(1, next.Calls);
        unitOfWork.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task<int>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransactionBehavior_ForANonGenericCommand_RunsInsideExecuteInTransactionAsync()
    {
        var unitOfWork = RunningUnitOfWork<Unit>();
        var next = new Next<Unit>(Unit.Value);

        await new TransactionBehavior<TestUnitCommand, Unit>(unitOfWork.Object)
            .Handle(new TestUnitCommand(), next.Invoke, Ct);

        Assert.Equal(1, next.Calls);
        unitOfWork.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task<Unit>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
