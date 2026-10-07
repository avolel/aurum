using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// Saves a classified <see cref="PriceEvent"/> unless the same symbol and window already has one
/// inside the waiting period (D-18).
/// </summary>
/// <remarks>
/// The waiting period is checked in the database so it survives a restart; an in-memory "last
/// emitted" time would let the first poll after a restart repeat the event. It is measured on
/// <c>WindowEndedAt</c> (price time), not the wall clock, so replaying the same prices gives the
/// same events. <c>ON CONFLICT</c> stays as the safety net for an exact repeat. Raw SQL bypasses
/// <c>ApplyAuditFields</c>, so the timestamps are passed in.
/// </remarks>
internal static class PriceEventSql
{
    /// <summary>Returns true when a row was written, false when the waiting period or the unique rule held it back.</summary>
    public static async Task<bool> InsertAsync(
        AurumDbContext db, PriceEvent e, TimeSpan cooldown, DateTimeOffset now, CancellationToken ct)
    {
        var quietUntilAfter = e.WindowEndedAt - cooldown;

        // CAST on Volatility: a null parameter carries no type, and Postgres reads an untyped value
        // in a SELECT list as text, which a numeric column refuses.
        var written = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO price_events
                ("Symbol", "DetectedAt", "WindowCode", "WindowStartedAt", "WindowEndedAt", "Direction",
                 "StartMid", "EndMid", "DeltaAbsolute", "DeltaPercent", "VelocityPercentPerMinute",
                 "Volatility", "SampleCount", "SourceCode", "BaselineSourceCode", "IsCrossSource",
                 "ThresholdProfile", "TriggeredRule", "CreatedAt", "UpdatedAt")
            SELECT {e.Symbol}, {e.DetectedAt}, {e.WindowCode}, {e.WindowStartedAt}, {e.WindowEndedAt}, {e.Direction},
                   {e.StartMid}, {e.EndMid}, {e.DeltaAbsolute}, {e.DeltaPercent}, {e.VelocityPercentPerMinute},
                   CAST({e.Volatility} AS numeric), {e.SampleCount}, {e.SourceCode}, {e.BaselineSourceCode}, {e.IsCrossSource},
                   {e.ThresholdProfile}, {e.TriggeredRule}, {now}, {now}
            WHERE NOT EXISTS (
                SELECT 1 FROM price_events
                 WHERE "Symbol" = {e.Symbol} AND "WindowCode" = {e.WindowCode}
                   AND "WindowEndedAt" > {quietUntilAfter})
            ON CONFLICT ("Symbol", "WindowCode", "WindowEndedAt") DO NOTHING
            """, ct);

        return written == 1;
    }
}
