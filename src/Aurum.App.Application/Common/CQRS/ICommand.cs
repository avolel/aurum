using MediatR;

namespace Aurum.App.Application.Common.CQRS;

/// <summary>
/// A request that mutates state. <c>TransactionBehavior</c> keys off this interface, so a command
/// that implements <see cref="IRequest{TResponse}"/> directly runs outside a transaction and looks
/// identical at the call site — which is the whole reason the marker exists.
/// </summary>
public interface ICommand<out TResponse> : IRequest<TResponse>;

/// <summary>A command with no meaningful return value.</summary>
public interface ICommand : IRequest<Unit>;
