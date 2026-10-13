using Aurum.App.Application.Prices.Queries;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;

namespace Aurum.App.Application.Prices.Validators;

public sealed class GetLivePriceQueryValidator : AbstractValidator<GetLivePriceQuery>
{
    public GetLivePriceQueryValidator()
    {
        RuleFor(q => q.Symbol)
            .Must(s => SupportedSymbol.All.Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Symbol must be one of: {string.Join(", ", SupportedSymbol.All)}.");
    }
}
