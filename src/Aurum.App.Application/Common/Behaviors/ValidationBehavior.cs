using FluentValidation;
using MediatR;

namespace Aurum.App.Application.Common.Behaviors;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for the request before the handler sees it.
/// </summary>
/// <remarks>
/// <para>
/// All validators run, and all their failures are aggregated into one
/// <see cref="ValidationException"/>. Stopping at the first failure would make a caller fix one
/// field per round trip.
/// </para>
/// <para>
/// A request with no validator passes straight through. That is a deliberate hole and the reason
/// "every command MUST have a validator" is a review rule rather than something the pipeline can
/// enforce: MediatR has no way to distinguish "this command has nothing to validate" from
/// "someone forgot the file".
/// </para>
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var applicable = validators as IList<IValidator<TRequest>> ?? validators.ToList();

        if (applicable.Count == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var results = await Task.WhenAll(
            applicable.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        var failures = results.SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}
