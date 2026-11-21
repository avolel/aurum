using Aurum.App.Infrastructure.Data.Entities.Macro;
using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Macro;

namespace Aurum.Api.Tests.Modules.Macro;

/// <remarks>No container: <c>FetchFrom</c>, <c>Fetches</c> and <c>NewMonths</c> are pure.</remarks>
public class MacroPollingServiceTests
{
    private static readonly DateOnly HistoryStart = new(2006, 1, 1);

    // 2026-11-13 08:30 New York.
    private static readonly DateTimeOffset CpiAt = FredRelease.Cpi.At(new DateOnly(2026, 11, 13));

    [Fact]
    public void FetchFrom_EmptySeries_ReturnsHistoryStart() =>
        Assert.Equal(HistoryStart, MacroPollingService.FetchFrom(null, HistoryStart));

    [Fact]
    public void FetchFrom_StoredSeries_ReturnsSixtyDaysBeforeNewest() =>
        Assert.Equal(new DateOnly(2026, 9, 2), MacroPollingService.FetchFrom(new DateOnly(2026, 11, 1), HistoryStart));

    [Fact]
    public void FetchFrom_NewestAcrossYearEnd_GoesBackIntoPreviousYear() =>
        Assert.Equal(new DateOnly(2025, 11, 2), MacroPollingService.FetchFrom(new DateOnly(2026, 1, 1), HistoryStart));

    [Fact]
    public void Fetches_BlsRelease_ReturnsBlsAfterOneMinuteThenFredAfterOneHour()
    {
        var fetches = MacroPollingService.Fetches([Row(FredRelease.Cpi.Code, CpiAt)], CpiAt.AddDays(-1));

        Assert.Equal(
            [(true, CpiAt.AddMinutes(1)), (false, CpiAt.AddHours(1))],
            fetches.Select(f => (f.FromBls, f.DueAt)));
    }

    [Fact]
    public void Fetches_Pce_ReturnsFredOnly()
    {
        var at = FredRelease.Pce.At(new DateOnly(2026, 11, 25));

        var fetch = Assert.Single(MacroPollingService.Fetches([Row(FredRelease.Pce.Code, at)], at.AddDays(-1)));
        Assert.False(fetch.FromBls);
    }

    [Fact]
    public void Fetches_AfterBlsDueTime_ReturnsOnlyTheFredFetch()
    {
        // Already handled up to 08:45: BLS's 08:31 fetch is done, FRED's 09:30 is still to come.
        var fetch = Assert.Single(MacroPollingService.Fetches([Row(FredRelease.Cpi.Code, CpiAt)], CpiAt.AddMinutes(15)));
        Assert.False(fetch.FromBls);
    }

    [Fact]
    public void Fetches_DueExactlyAtCursor_IsNotRunAgain() =>
        Assert.Empty(MacroPollingService.Fetches([Row(FredRelease.Cpi.Code, CpiAt)], CpiAt.AddHours(1)));

    [Fact]
    public void Fetches_UnknownReleaseCode_IsSkipped() =>
        Assert.Empty(MacroPollingService.Fetches([Row("gdp", CpiAt)], CpiAt.AddDays(-1)));

    [Fact]
    public void Fetches_FomcRow_ReturnsNothing()
    {
        // A decision is a calendar row for item 6 only; DFF doesn't move until the next day.
        var at = FomcCalendar.At(new DateOnly(2026, 12, 9));
        Assert.Empty(MacroPollingService.Fetches([Row(FomcCalendar.Code, at)], at.AddDays(-1)));
    }

    [Fact]
    public void Fetches_SeveralRows_ReturnsEarliestFirst()
    {
        var jobsAt = FredRelease.Jobs.At(new DateOnly(2026, 12, 4));

        var fetches = MacroPollingService.Fetches(
            [Row(FredRelease.Jobs.Code, jobsAt), Row(FredRelease.Cpi.Code, CpiAt)], CpiAt.AddDays(-1));

        Assert.Equal(
            [FredRelease.Cpi, FredRelease.Cpi, FredRelease.Jobs, FredRelease.Jobs],
            fetches.Select(f => f.Release));
    }

    [Fact]
    public void NewMonths_NewerThanStored_ReturnsOnlyTheNewestMonth()
    {
        var fetched = new[]
        {
            new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 10, 1), 337.476m),
            new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 9, 1), 336.9m),
        };

        var row = Assert.Single(MacroPollingService.NewMonths(fetched, Stored((FredSeries.Cpi, new DateOnly(2026, 9, 1)))));
        Assert.Equal((new DateOnly(2026, 10, 1), 337.476m), (row.ObservedOn, row.Value));
    }

    [Fact]
    public void NewMonths_SameMonthAsStored_ReturnsNothing() =>
        Assert.Empty(MacroPollingService.NewMonths(
            [new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 9, 1), 336.9m)],
            Stored((FredSeries.Cpi, new DateOnly(2026, 9, 1)))));

    [Fact]
    public void NewMonths_NewestMonthHasNoValue_ReturnsNothing() =>
        // BLS's "-": the month is listed but not out yet, and September is already stored.
        Assert.Empty(MacroPollingService.NewMonths(
            [
                new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 10, 1), null),
                new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 9, 1), 336.9m),
            ],
            Stored((FredSeries.Cpi, new DateOnly(2026, 9, 1)))));

    [Fact]
    public void NewMonths_NothingStored_ReturnsNewestMonth() =>
        Assert.Single(MacroPollingService.NewMonths(
            [new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 10, 1), 337.476m)],
            Stored((FredSeries.Cpi, null))));

    [Fact]
    public void NewMonths_OneSeriesReady_ReturnsOnlyThatSeries()
    {
        var fetched = new[]
        {
            new BlsObservation(BlsSeries.Cpi, new DateOnly(2026, 10, 1), 337.476m),
            new BlsObservation(BlsSeries.CoreCpi, new DateOnly(2026, 9, 1), 339.1m),
        };

        var row = Assert.Single(MacroPollingService.NewMonths(fetched,
            Stored((FredSeries.Cpi, new DateOnly(2026, 9, 1)), (FredSeries.CoreCpi, new DateOnly(2026, 9, 1)))));
        Assert.Equal(BlsSeries.Cpi, row.Series);
    }

    private static MacroRelease Row(string code, DateTimeOffset at) => new() { ReleaseCode = code, ScheduledAt = at };

    private static Dictionary<FredSeries, DateOnly?> Stored(params (FredSeries Series, DateOnly? Newest)[] rows) =>
        rows.ToDictionary(r => r.Series, r => r.Newest);
}
