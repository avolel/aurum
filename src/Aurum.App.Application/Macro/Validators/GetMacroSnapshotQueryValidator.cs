using Aurum.App.Application.Macro.Queries;
using FluentValidation;

namespace Aurum.App.Application.Macro.Validators;

/// <remarks>Takes the clock by constructor, like <c>GetPriceHistoryQueryValidator</c>. Never <c>DateTimeOffset.UtcNow</c>.</remarks>
public sealed class GetMacroSnapshotQueryValidator : AbstractValidator<GetMacroSnapshotQuery>
{
    public GetMacroSnapshotQueryValidator(TimeProvider clock)
    {
        // A future AsOf would quietly answer with today's figures and look like a forecast.
        RuleFor(q => q.AsOf)
            .Must(asOf => asOf <= clock.GetUtcNow())
            .When(q => q.AsOf is not null)
            .WithMessage("'asOf' must not be in the future. Leave it out to mean now.");
    }
}
