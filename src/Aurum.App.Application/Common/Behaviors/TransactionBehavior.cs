using Aurum.App.Application.Common.CQRS;
using Aurum.App.Infrastructure.Data.Repositories;
using MediatR;

namespace Aurum.App.Application.Common.Behaviors;

/// <summary>
/// Wraps commands in a database transaction. Queries pass straight through.
/// </summary>
/// <remarks>
/// Keyed on <see cref="ICommand{T}"/>: a command implementing <c>IRequest&lt;T&gt;</c> directly gets
/// no transaction. Handlers must not open their own, and should make HTTP calls before writing.
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly bool IsCommand =
        typeof(TRequest).GetInterfaces().Any(i =>
            i == typeof(ICommand) ||
            (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!IsCommand)
        {
            return await next();
        }

        return await unitOfWork.ExecuteInTransactionAsync(_ => next(), cancellationToken);
    }
}
