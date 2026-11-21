namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>
/// The Fed's rate decisions: the last day of each scheduled FOMC meeting, announced at 14:00 New York.
/// Saved to <c>macro_releases</c> under <see cref="Code"/> so item 6 can tell the Fed decided that day.
/// Nothing is fetched for them (D-28).
/// </summary>
/// <remarks>
/// A hand-kept list because no API serves it: FRED's release 101 is updated daily, and the Fed's page
/// is HTML only. Copied from https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm on
/// 2026-11-20. 2027 and 2028 are tentative there. Emergency meetings (March 2020) are not in it.
/// When the last date passes, every macro pass logs an <c>FomcCalendarStale</c> warning.
/// </remarks>
internal static class FomcCalendar
{
    /// <summary>The <c>macro_releases.release_code</c>. Not a <see cref="FredRelease"/>, so no fetch.</summary>
    public const string Code = "fomc";

    public static readonly TimeOnly DecisionTime = new(14, 0);

    // The decision day only: the first day of a two-day meeting announces nothing.
    public static readonly IReadOnlyList<DateOnly> Meetings =
    [
        new(2025, 1, 29), new(2025, 3, 19), new(2025, 5, 7), new(2025, 6, 18),
        new(2025, 7, 30), new(2025, 9, 17), new(2025, 10, 29), new(2025, 12, 10),
        new(2026, 1, 28), new(2026, 3, 18), new(2026, 4, 29), new(2026, 6, 17),
        new(2026, 7, 29), new(2026, 9, 16), new(2026, 10, 28), new(2026, 12, 9),
        // Tentative.
        new(2027, 1, 27), new(2027, 3, 17), new(2027, 4, 28), new(2027, 6, 9),
        new(2027, 7, 28), new(2027, 9, 15), new(2027, 10, 27), new(2027, 11, 17),
        new(2028, 1, 26),
    ];

    /// <summary>The decision time on <paramref name="day"/>, in UTC.</summary>
    public static DateTimeOffset At(DateOnly day) => NewYorkTime.At(day, DecisionTime);

    /// <summary>False once every listed decision is at or before <paramref name="now"/>: the list needs new dates.</summary>
    public static bool HasMeetingAfter(DateTimeOffset now) => Meetings.Any(d => At(d) > now);
}
