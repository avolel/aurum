namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>A FRED release: its id, the New York time it publishes, and the series it carries.</summary>
/// <remarks>
/// FRED gives a release's dates but no time of day, so the time is kept here. No Fed decision: FRED's
/// release 101 (FOMC Press Release) is updated daily, so its dates are not meeting days (checked
/// 2026-11-16).
/// </remarks>
public sealed record FredRelease(string Code, int FredId, TimeOnly PublishTime, IReadOnlyList<FredSeries> Series)
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static readonly FredRelease Cpi =
        new("cpi", 10, new TimeOnly(8, 30), [FredSeries.Cpi, FredSeries.CoreCpi]);

    public static readonly FredRelease Jobs =
        new("jobs", 50, new TimeOnly(8, 30), [FredSeries.NonfarmPayrolls, FredSeries.UnemploymentRate]);

    // From BEA, not BLS, so there is no release-morning BLS figure for it.
    public static readonly FredRelease Pce =
        new("pce", 54, new TimeOnly(8, 30), [FredSeries.Pce]);

    // Declared last: static fields initialise in text order.
    public static readonly IReadOnlyList<FredRelease> All = [Cpi, Jobs, Pce];

    /// <summary>The release that carries <paramref name="series"/>. Throws for a market series.</summary>
    public static FredRelease Of(FredSeries series) => All.Single(r => r.Series.Contains(series));

    /// <summary><see cref="PublishTime"/> on <paramref name="day"/>, in UTC.</summary>
    public DateTimeOffset At(DateOnly day)
    {
        var local = day.ToDateTime(PublishTime);
        // GetUtcOffset picks EST or EDT for that date. UTC because Npgsql only writes offset-zero values.
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }
}
