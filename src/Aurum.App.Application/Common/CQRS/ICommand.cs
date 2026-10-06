using MediatR;

namespace Aurum.App.Application.Common.CQRS;

/// <summary>
/// A request that changes state. <c>TransactionBehavior</c> wraps only requests marked with this.
/// </summary>
public interface ICommand<out TResponse> : IRequest<TResponse>;

/// <summary>A command with no meaningful return value.</summary>
public interface ICommand : IRequest<Unit>;
