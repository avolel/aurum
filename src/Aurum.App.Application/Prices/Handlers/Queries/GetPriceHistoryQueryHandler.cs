using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Data;
using Aurum.App.SharedKernel.Common;
using Aurum.App.SharedKernel.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Application.Prices.Handlers.Queries;

/// <summary>
/// One page of stored ticks for a symbol, oldest first, paged by a keyset cursor.
/// </summary>
/// <remarks>
/// Contract (pinned by <c>GetPriceHistoryQueryHandlerTests</c>):
/// <list type="bullet">
/// <item>The caller's symbol matches ignoring case, via <see cref="SupportedSymbol.Find"/>, and is
/// then compared exactly in SQL.</item>
/// <item>Rows with <c>From &lt;= ObservedAt &lt; To</c>.
/// Half-open, so two adjacent ranges never return the same tick. <c>To</c> null means
/// <c>clock.GetUtcNow()</c>.</item>
/// <item>Ordered by <c>ObservedAt</c>, then <c>Id</c>. <c>Id</c> breaks ties: two sources can report the
/// same <c>ObservedAt</c>.</item>
/// <item>With a cursor, only rows strictly after it: <c>(ObservedAt, Id) &gt; (cursor.Time, cursor.Id)</c>.
/// EF Core does not translate C# tuple comparison; write it out.</item>
/// <item>Page size is <see cref="PaginationHelper.ClampPageSize"/> of <c>Limit</c>. Read one row more
/// than that to learn whether another page exists, and do not return the extra row.</item>
/// <item><c>NextCursor</c> is the last returned row's <c>(ObservedAt, Id)</c>, encoded with
/// <see cref="Cursor.Encode"/>, or null when no row follows it.</item>
/// <item><c>AsNoTracking()</c>: read-only. The validator has already checked the cursor decodes.</item>
/// </list>
/// </remarks>
public sealed class GetPriceHistoryQueryHandler(AurumDbContext db, TimeProvider clock)
    : IRequestHandler<GetPriceHistoryQuery, CursorPage<PriceTickDto>>
{
    public async Task<CursorPage<PriceTickDto>> Handle(GetPriceHistoryQuery request, CancellationToken cancellationToken)
    {
        // The validator has checked the symbol, From and the cursor, so the "!"s cannot fail.
        var symbol = SupportedSymbol.Find(request.Symbol)!;
        var from = request.From!.Value;
        var to = request.To ?? clock.GetUtcNow();
        var limit = PaginationHelper.ClampPageSize(request.Limit);

        var ticks = db.PriceTicks.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.ObservedAt >= from && t.ObservedAt < to);

        if (request.Cursor is not null)
        {
            Cursor.TryDecode(request.Cursor, out var after);

            // (ObservedAt, Id) > (after.Time, after.Id), written out: EF Core can't translate tuples.
            ticks = ticks.Where(t => t.ObservedAt > after.Time || (t.ObservedAt == after.Time && t.Id > after.Id));
        }

        // One extra row says whether another page exists without a COUNT.
        var rows = await ticks
            .OrderBy(t => t.ObservedAt).ThenBy(t => t.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(limit).ToList();
        var nextCursor = rows.Count > limit ? new Cursor(page[^1].ObservedAt, page[^1].Id).Encode() : null;

        return new CursorPage<PriceTickDto>(
            page.Select(t => new PriceTickDto(t.ObservedAt, t.ReceivedAt, t.Mid, t.Bid, t.Ask, t.SourceCode)).ToList(),
            nextCursor);
    }
}
