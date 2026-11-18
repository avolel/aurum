using Aurum.App.Application.Macro.DTOs;
using Aurum.App.Application.Macro.Queries;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing.Macro;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Application.Macro.Handlers.Queries;

/// <summary>
/// For each series in <see cref="FredSeries.All"/>, the newest stored row with a value that was
/// known at <c>AsOf</c>. A series with no such row is left out.
/// </summary>
/// <remarks>
/// "Known" (D-27, pinned by <c>GetMacroSnapshotQueryHandlerTests</c>):
/// <list type="bullet">
/// <item>Release series: <c>ReleasedAt &lt;= AsOf</c>. A figure published after a move can't explain it.</item>
/// <item>Market series: <c>ObservedOn</c> before <c>AsOf</c>'s New York date. A daily figure isn't
/// final until that US day ends.</item>
/// </list>
/// "Newest" is the latest <c>ObservedOn</c>. <c>AgeDays</c> is worked out here only, so the endpoint
/// and the explanation prompt can't disagree.
/// </remarks>
public sealed class GetMacroSnapshotQueryHandler(AurumDbContext db, TimeProvider clock)
    : IRequestHandler<GetMacroSnapshotQuery, IReadOnlyList<MacroFigureDto>>
{
    private static readonly List<string> ReleaseCodes =
        FredSeries.All.Where(s => s.IsRelease).Select(s => s.Code).ToList();

    private static readonly List<string> MarketCodes =
        FredSeries.All.Where(s => !s.IsRelease).Select(s => s.Code).ToList();

    public async Task<IReadOnlyList<MacroFigureDto>> Handle(GetMacroSnapshotQuery request, CancellationToken cancellationToken)
    {
        // UTC because Npgsql refuses a non-zero offset for timestamptz.
        var asOf = request.AsOf?.ToUniversalTime() ?? clock.GetUtcNow();
        var asOfDay = FredRelease.NewYorkDay(asOf);

        var newest = await db.MacroObservations.AsNoTracking()
            .Where(o => o.Value != null && o.Series.Provider == FredSeries.Provider)
            .Where(o => (ReleaseCodes.Contains(o.Series.Code) && o.ReleasedAt <= asOf)
                        || (MarketCodes.Contains(o.Series.Code) && o.ObservedOn < asOfDay))
            .GroupBy(o => o.Series.Code)
            .Select(g => g
                .OrderByDescending(o => o.ObservedOn)
                .Select(o => new { o.Series.Code, o.Series.Title, o.Series.Units, Value = o.Value!.Value, o.ObservedOn, o.ReleasedAt })
                .First())
            .ToDictionaryAsync(r => r.Code, cancellationToken);

        return FredSeries.All
            .Where(s => newest.ContainsKey(s.Code))
            .Select(s => newest[s.Code])
            .Select(r => new MacroFigureDto(
                r.Code, r.Title, r.Units, r.Value, r.ObservedOn, r.ReleasedAt,
                AgeDays: r.ReleasedAt is { } releasedAt
                    ? (int)(asOf - releasedAt).TotalDays
                    : asOfDay.DayNumber - r.ObservedOn.DayNumber))
            .ToList();
    }
}
