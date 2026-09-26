using MediatR;

namespace Aurum.App.Application.Common.CQRS;

/// <summary>
/// A read-only request. Never wrapped in a transaction.
/// </summary>
/// <remarks>
/// Naming follows the interface, not the HTTP verb: a verify or check that does not mutate state
/// is a query even when it is called with POST.
/// </remarks>
public interface IQuery<out TResponse> : IRequest<TResponse>;
