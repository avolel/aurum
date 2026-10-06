using MediatR;

namespace Aurum.App.Application.Common.CQRS;

/// <summary>
/// A read-only request. Never wrapped in a transaction.
/// </summary>
/// <remarks>A check that changes nothing is a query, even over POST.</remarks>
public interface IQuery<out TResponse> : IRequest<TResponse>;
