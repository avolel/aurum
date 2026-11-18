namespace Aurum.App.Application.Macro.DTOs;

/// <param name="ObservedOn">The period the value describes, e.g. 2026-10-01 for October CPI.</param>
/// <param name="ReleasedAt">When the figure was published. Null for market series.</param>
/// <param name="AgeDays">Whole days from <c>AsOf</c> back to <c>ReleasedAt</c>, or to <c>ObservedOn</c> for market series (D-27).</param>
public sealed record MacroFigureDto(
    string Code,
    string Title,
    string? Units,
    decimal Value,
    DateOnly ObservedOn,
    DateTimeOffset? ReleasedAt,
    int AgeDays);
