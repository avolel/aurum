using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using FluentValidation;

namespace Aurum.App.Application.Prices.Validators;

/// <remarks>
/// Takes the clock by constructor; assembly scanning registers validators in the container, so it
/// is injected like anywhere else. Never <c>DateTimeOffset.UtcNow</c> (D-7).
/// </remarks>
public sealed class GetPriceHistoryQueryValidator : AbstractValidator<GetPriceHistoryQuery>
{
    public GetPriceHistoryQueryValidator(TimeProvider clock)
    {
        RuleFor(q => q.Symbol)
            .Must(s => SupportedSymbol.All.Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Symbol must be one of: {string.Join(", ", SupportedSymbol.All)}.");

        RuleFor(q => q.From).NotNull().WithMessage("'from' is required.");

        // Older than retention would come back empty or partial and look like "no prices then".
        RuleFor(q => q.From)
            .Must(from => from >= clock.GetUtcNow() - PriceTick.Retention)
            .When(q => q.From is not null)
            .WithMessage(_ =>
                $"The database keeps {PriceTick.Retention.TotalDays:0} days of prices, so 'from' must be on or after " +
                $"{clock.GetUtcNow() - PriceTick.Retention:O}.");

        RuleFor(q => q.To)
            .Must((q, to) => q.From < (to ?? clock.GetUtcNow()))
            .When(q => q.From is not null)
            .WithMessage("'to' must be after 'from'. When 'to' is left out it is now, so 'from' must be in the past.");

        RuleFor(q => q.Cursor)
            .Must(cursor => Cursor.TryDecode(cursor!, out _))
            .When(q => q.Cursor is not null)
            .WithMessage("'cursor' is not one this server issued. Pass a page's nextCursor unchanged.");
    }
}
