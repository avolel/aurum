using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;

namespace Aurum.App.Application.Prices.Validators;

public sealed class GetPriceEventsQueryValidator : AbstractValidator<GetPriceEventsQuery>
{
    public GetPriceEventsQueryValidator()
    {
        RuleFor(q => q.Symbol)
            .Must(s => SupportedSymbol.Find(s) is not null)
            .WithMessage($"Symbol must be one of: {string.Join(", ", SupportedSymbol.All)}.");

        // Exact match: the codes are stored as written, and "5M" is not a code anywhere else.
        RuleFor(q => q.Window)
            .Must(window => DeltaWindow.All.Any(w => w.Code == window))
            .When(q => q.Window is not null)
            .WithMessage($"'window' must be one of: {string.Join(", ", DeltaWindow.All.Select(w => w.Code))}.");

        RuleFor(q => q.MinPercent)
            .GreaterThanOrEqualTo(0)
            .When(q => q.MinPercent is not null)
            .WithMessage("'minPercent' is a size and cannot be negative; falls are matched by their size too.");

        RuleFor(q => q.Cursor)
            .Must(cursor => Cursor.TryDecode(cursor!, out _))
            .When(q => q.Cursor is not null)
            .WithMessage("'cursor' is not one this server issued. Pass a page's nextCursor unchanged.");
    }
}
