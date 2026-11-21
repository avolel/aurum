namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>
/// The only place that converts between New York wall-clock time and UTC. US releases and the Fed's
/// decisions are set in New York time; the database stores UTC.
/// </summary>
/// <remarks>
/// The zone is looked up once, so a host without tzdata throws <see cref="TypeInitializationException"/>
/// on first use; <c>aspnet:10.0</c> has it (checked 2026-11-13).
/// </remarks>
public static class NewYorkTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary><paramref name="time"/> on <paramref name="day"/> in New York, as UTC.</summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time);
        // GetUtcOffset picks EST or EDT for that date. UTC because Npgsql only writes offset-zero values.
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The New York date of <paramref name="at"/>; the reverse of <see cref="At"/>'s day.</summary>
    public static DateOnly Day(DateTimeOffset at) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
}
