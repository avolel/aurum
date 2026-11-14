using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Macro;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>
/// Saves one series' FRED rows into <c>macro_series</c> and <c>macro_observations</c>. Database only;
/// the caller fetches.
/// </summary>
/// <remarks>
/// Loads, compares in C# and saves through EF rather than one raw upsert, because a changed release
/// value has to be logged with its old value (D-23). Safe only with one writer, which holds while the
/// app is single-instance and FRED is its only macro writer.
/// </remarks>
internal sealed class FredImporter(AurumDbContext db, ILogger<FredImporter> logger)
{
    // Every release series publishes at 08:30 New York time.
    private static readonly TimeOnly ReleaseTime = new(8, 30);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <returns>Rows inserted and updated, for the caller's log line.</returns>
    public async Task<(int Inserted, int Updated)> SaveAsync(
        FredSeries series, IReadOnlyList<FredObservation> rows, CancellationToken ct)
    {
        var seriesRow = await db.MacroSeries
            .SingleOrDefaultAsync(s => s.Provider == FredSeries.Provider && s.Code == series.Code, ct);

        if (seriesRow is null)
        {
            seriesRow = new MacroSeries
            {
                Provider = FredSeries.Provider,
                Code = series.Code,
                Title = series.Title,
                Units = series.Units,
                Frequency = series.Frequency,
            };            
            db.MacroSeries.Add(seriesRow);
        }

        // Tracked, not AsNoTracking: changed rows are updated in place.
        var dates = rows.Select(r => r.ObservedOn).ToList();
        var existing = seriesRow.Id == 0
            ? []
            : await db.MacroObservations
                .Where(o => o.MacroSeriesId == seriesRow.Id && dates.Contains(o.ObservedOn))
                .ToDictionaryAsync(o => o.ObservedOn, ct);

        int inserted = 0, updated = 0;
        foreach (var row in rows)
        {
            var releasedAt = ReleasedAt(row.ReleasedOn);

            if (!existing.TryGetValue(row.ObservedOn, out var stored))
            {
                seriesRow.Observations.Add(new MacroObservation
                {
                    ObservedOn = row.ObservedOn,
                    Value = row.Value,
                    ReleasedAt = releasedAt,
                });
                inserted++;
                continue;
            }

            if (stored.Value == row.Value && stored.ReleasedAt == releasedAt)
            {
                continue;
            }

            // A first published value should never change, except where FRED replaces BLS's
            // release-morning row (D-23). Market series are corrected routinely, so no warning.
            if (series.IsRelease && stored.Value != row.Value)
            {
                logger.LogWarning(
                    "FRED's {Series} for {ObservedOn:yyyy-MM-dd} is {New}; replacing stored {Old}.",
                    series.Code, row.ObservedOn, row.Value, stored.Value);
            }

            stored.Value = row.Value;
            stored.ReleasedAt = releasedAt;
            updated++;
        }

        // ApplyAuditFields stamps CreatedAt/UpdatedAt from the injected TimeProvider.
        await db.SaveChangesAsync(ct);
        return (inserted, updated);
    }

    /// <summary>08:30 New York time on <paramref name="releasedOn"/>, in UTC. Null for market series.</summary>
    internal static DateTimeOffset? ReleasedAt(DateOnly? releasedOn)
    {
        if (releasedOn is not { } day)
        {
            return null;
        }

        var local = day.ToDateTime(ReleaseTime);
        // GetUtcOffset picks EST or EDT for that date. UTC because Npgsql only writes offset-zero values.
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }
}
