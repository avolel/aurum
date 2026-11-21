using Aurum.App.Infrastructure.Pricing.Macro;

namespace Aurum.Api.Tests.Modules.Macro;

/// <remarks>No container: the definitions and the time rule are pure.</remarks>
public class FredReleaseTests
{
    [Fact]
    public void At_Winter_Is0830EasternStandardTime() =>
        Assert.Equal(
            new DateTimeOffset(2025, 2, 12, 13, 30, 0, TimeSpan.Zero),
            FredRelease.Cpi.At(new DateOnly(2025, 2, 12)));

    [Fact]
    public void At_Summer_Is0830EasternDaylightTime() =>
        Assert.Equal(
            new DateTimeOffset(2025, 7, 15, 12, 30, 0, TimeSpan.Zero),
            FredRelease.Cpi.At(new DateOnly(2025, 7, 15)));

    [Fact]
    public void NewYorkTimeDay_EveningInNewYork_IsThatDayNotTheUtcDay() =>
        // 2025-02-12 20:00 New York is already the 13th in UTC.
        Assert.Equal(new DateOnly(2025, 2, 12),
            NewYorkTime.Day(new DateTimeOffset(2025, 2, 13, 1, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void NewYorkTimeDay_OfAt_ReturnsTheSameDay()
    {
        var day = new DateOnly(2025, 7, 15);
        Assert.Equal(day, NewYorkTime.Day(FredRelease.Cpi.At(day)));
    }

    [Fact]
    public void All_EveryReleaseSeries_IsInExactlyOneRelease()
    {
        // FredImporter.ReleasedAt throws for a release series no release carries.
        foreach (var series in FredSeries.All.Where(s => s.IsRelease))
        {
            Assert.Single(FredRelease.All, r => r.Series.Contains(series));
        }
    }

    [Fact]
    public void All_MarketSeries_AreInNoRelease() =>
        Assert.DoesNotContain(FredRelease.All.SelectMany(r => r.Series), s => !s.IsRelease);

    [Fact]
    public void All_Codes_AreUnique() =>
        Assert.Equal(FredRelease.All.Count, FredRelease.All.Select(r => r.Code).Distinct().Count());
}
