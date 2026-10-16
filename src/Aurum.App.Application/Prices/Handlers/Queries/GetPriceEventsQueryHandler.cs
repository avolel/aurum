using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Data;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Application.Prices.Handlers.Queries;

/// <summary>
/// One page of saved price-move events, newest first, paged by a keyset cursor.
/// </summary>
/// <remarks>
/// Ordered by <c>DetectedAt</c>, then <c>Id</c>, both descending: one poll saves an event per
/// window that fired, all with the same <c>DetectedAt</c>, so <c>Id</c> is what keeps a page
/// boundary between them from repeating or skipping one. The cursor is the last returned row's
/// <c>(DetectedAt, Id)</c>; the next page is strictly before it.
/// </remarks>
public sealed class GetPriceEventsQueryHandler(AurumDbContext db)
    : IRequestHandler<GetPriceEventsQuery, CursorPage<PriceEventDto>>
{
    public async Task<CursorPage<PriceEventDto>> Handle(GetPriceEventsQuery request, CancellationToken cancellationToken)
    {
        // The validator has checked the symbol and the cursor, so the "!" cannot fail.
        var symbol = SupportedSymbol.Find(request.Symbol)!;
        var limit = PaginationHelper.ClampPageSize(request.Limit);

        var events = db.PriceEvents.AsNoTracking().Where(e => e.Symbol == symbol);

        if (request.Window is { } window)
        {
            events = events.Where(e => e.WindowCode == window);
        }

        if (request.MinPercent is { } minPercent)
        {
            // Size of the move, so falls count as well as rises. EF translates Math.Abs to abs().
            events = events.Where(e => Math.Abs(e.DeltaPercent) >= minPercent);
        }

        if (request.Cursor is not null)
        {
            Cursor.TryDecode(request.Cursor, out var before);

            // (DetectedAt, Id) < (before.Time, before.Id), written out: EF Core can't translate tuples.
            events = events.Where(e => e.DetectedAt < before.Time || (e.DetectedAt == before.Time && e.Id < before.Id));
        }

        // One extra row says whether another page exists without a COUNT.
        var rows = await events
            .OrderByDescending(e => e.DetectedAt).ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(limit).ToList();
        var nextCursor = rows.Count > limit ? new Cursor(page[^1].DetectedAt, page[^1].Id).Encode() : null;

        return new CursorPage<PriceEventDto>(
            page.Select(e => new PriceEventDto(
                e.Symbol, e.DetectedAt, e.WindowCode, e.WindowStartedAt, e.WindowEndedAt, e.Direction,
                e.StartMid, e.EndMid, e.DeltaAbsolute, e.DeltaPercent, e.VelocityPercentPerMinute,
                e.Volatility, e.SampleCount, e.SourceCode, e.BaselineSourceCode, e.IsCrossSource,
                e.ThresholdProfile, e.TriggeredRule)).ToList(),
            nextCursor);
    }
}
