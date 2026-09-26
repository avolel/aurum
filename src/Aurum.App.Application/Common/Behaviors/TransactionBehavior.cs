using Aurum.App.Application.Common.CQRS;
using Aurum.App.Infrastructure.Data.Repositories;
using MediatR;

namespace Aurum.App.Application.Common.Behaviors;

/// <summary>
/// Wraps commands in a database transaction. Queries pass straight through.
/// </summary>
/// <remarks>
/// <para>
/// The discrimination is on <see cref="ICommand{T}"/>, not on the HTTP verb or the type name, so a
/// request that implements <c>IRequest&lt;T&gt;</c> directly gets no transaction and looks
/// identical everywhere else. That is why the naming rule ("if it implements ICommand, call it
/// *Command") is a merge blocker rather than a style preference.
/// </para>
/// <para>
/// Command handlers must therefore not manage transactions themselves, and external HTTP calls
/// belong before the writes in a handler rather than inside them — a provider timing out should
/// not be holding a database transaction open.
/// </para>
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
