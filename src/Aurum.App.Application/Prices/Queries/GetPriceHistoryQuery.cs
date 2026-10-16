using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.SharedKernel.Common;

namespace Aurum.App.Application.Prices.Queries;

/// <summary>Stored ticks for one symbol in <c>[From, To)</c>, oldest first, one page at a time.</summary>
/// <param name="From">Nullable only so a missing value reaches the validator as "required".</param>
/// <param name="To">Null means now, by the injected clock.</param>
/// <param name="Limit">Clamped, not rejected: <see cref="PaginationHelper.ClampPageSize"/>.</param>
/// <param name="Cursor">The previous page's <c>NextCursor</c>; null for the first page.</param>
public sealed record GetPriceHistoryQuery(
    string Symbol,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Limit,
    string? Cursor) : IQuery<CursorPage<PriceTickDto>>;
