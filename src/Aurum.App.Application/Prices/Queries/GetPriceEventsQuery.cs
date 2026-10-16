using Aurum.App.Application.Common.CQRS;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.SharedKernel.Common;

namespace Aurum.App.Application.Prices.Queries;

/// <summary>Saved price-move events for one symbol, newest first, one page at a time.</summary>
/// <param name="Window">A <c>DeltaWindow.Code</c> such as <c>5m</c>; null for every window.</param>
/// <param name="MinPercent">Smallest move to include, as a size: a fall of 0.4% passes 0.3.</param>
/// <param name="Limit">Clamped, not rejected: <see cref="PaginationHelper.ClampPageSize"/>.</param>
/// <param name="Cursor">The previous page's <c>NextCursor</c>; null for the first page.</param>
public sealed record GetPriceEventsQuery(
    string Symbol,
    string? Window,
    decimal? MinPercent,
    int? Limit,
    string? Cursor) : IQuery<CursorPage<PriceEventDto>>;
