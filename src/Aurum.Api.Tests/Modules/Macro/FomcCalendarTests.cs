using Aurum.App.Infrastructure.Pricing.Macro;

namespace Aurum.Api.Tests.Modules.Macro;

/// <remarks>No container: the list and the time rule are pure.</remarks>
public class FomcCalendarTests
{
    [Fact]
    public void At_Summer_Is1400EasternDaylightTime() =>
        Assert.Equal(
            new DateTimeOffset(2026, 6, 17, 18, 0, 0, TimeSpan.Zero),
            FomcCalendar.At(new DateOnly(2026, 6, 17)));

    [Fact]
    public void Meetings_AreAscendingAndUnique() =>
        Assert.Equal(FomcCalendar.Meetings.Order().Distinct(), FomcCalendar.Meetings);

    [Fact]
    public void Meetings_AreAllWednesdays() =>
        // Every scheduled decision since 2025 lands on a Wednesday, so a different day is a typo.
        Assert.All(FomcCalendar.Meetings, d => Assert.Equal(DayOfWeek.Wednesday, d.DayOfWeek));

    [Fact]
    public void Code_IsNotAFredRelease() =>
        // In FredRelease.All, every pass would ask FRED for the dates of a release it doesn't have.
        Assert.DoesNotContain(FredRelease.All, r => r.Code == FomcCalendar.Code);

    [Fact]
    public void HasMeetingAfter_AfterLastDecision_IsFalse() =>
        // This false is what logs FomcCalendarStale each pass.
        Assert.False(FomcCalendar.HasMeetingAfter(FomcCalendar.At(FomcCalendar.Meetings[^1])));

    [Fact]
    public void HasMeetingAfter_BeforeLastDecision_IsTrue() =>
        Assert.True(FomcCalendar.HasMeetingAfter(FomcCalendar.At(FomcCalendar.Meetings[^1]).AddMinutes(-1)));
}
