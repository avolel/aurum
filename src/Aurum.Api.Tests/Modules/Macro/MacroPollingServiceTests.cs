using Aurum.App.Infrastructure.Pricing.Jobs;

namespace Aurum.Api.Tests.Modules.Macro;

/// <remarks>No container: <c>FetchFrom</c> is pure.</remarks>
public class MacroPollingServiceTests
{
    private static readonly DateOnly HistoryStart = new(2006, 1, 1);

    [Fact]
    public void FetchFrom_EmptySeries_ReturnsHistoryStart() =>
        Assert.Equal(HistoryStart, MacroPollingService.FetchFrom(null, HistoryStart));

    [Fact]
    public void FetchFrom_StoredSeries_ReturnsSixtyDaysBeforeNewest() =>
        Assert.Equal(new DateOnly(2026, 9, 2), MacroPollingService.FetchFrom(new DateOnly(2026, 11, 1), HistoryStart));

    [Fact]
    public void FetchFrom_NewestAcrossYearEnd_GoesBackIntoPreviousYear() =>
        Assert.Equal(new DateOnly(2025, 11, 2), MacroPollingService.FetchFrom(new DateOnly(2026, 1, 1), HistoryStart));
}
