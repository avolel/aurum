using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The classifier on hand-built snapshots. No container, no clock, sub-second.
/// </summary>
public class SignificanceClassifierTests
{
    private const string Primary = ApiNinjasSource.SourceCode;
    private const string Backup = GoldApiIoSource.SourceCode;

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static SignificanceOptions Options()
    {
        var options = new SignificanceOptions { ThresholdProfile = "test-v1", CrossSourceMagnitudeMultiplier = 2m };
        options.Windows["5m"] = new SignificanceWindowOptions { MinPercent = 0.25m, Cooldown = TimeSpan.FromMinutes(5) };
        options.Windows["1h"] = new SignificanceWindowOptions { MinPercent = 0.75m, Cooldown = TimeSpan.FromHours(1) };
        return options;
    }

    // Start and end prices are worked back from the percent so the row's numbers agree with each other.
    private static WindowDelta Delta(DeltaWindow window, decimal percent, bool crossSource = false)
    {
        const decimal startMid = 4_000m;
        var endMid = startMid * (1 + percent / 100);
        var start = new Sample(Now - window.Length, startMid, 0);
        var end = new Sample(Now, endMid, crossSource ? (byte)1 : (byte)0);

        return new WindowDelta(
            window, start, end,
            DeltaAbsolute: endMid - startMid,
            DeltaPercent: percent,
            VelocityPercentPerMinute: percent / (decimal)window.Length.TotalMinutes,
            Volatility: 0.05,
            SampleCount: 6,
            CrossSource: crossSource,
            StartSourceCode: crossSource ? Backup : Primary,
            EndSourceCode: Primary);
    }

    // Every window null except the ones given, the shape DeltaEngine returns.
    private static DeltaSnapshot Snapshot(params WindowDelta[] deltas)
    {
        var windows = DeltaWindow.All.ToDictionary(w => w, w => deltas.FirstOrDefault(d => d.Window == w));
        return new DeltaSnapshot(SupportedSymbol.Gold, Now, windows, DroppedOutOfOrder: 0);
    }

    [Fact]
    public void Cross_source_delta_requires_higher_magnitude()
    {
        var sameSource = SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FiveMinutes, 0.30m)), Options());
        var crossSource = SignificanceClassifier.Classify(
            Snapshot(Delta(DeltaWindow.FiveMinutes, 0.30m, crossSource: true)), Options());

        Assert.Single(sameSource);
        Assert.Empty(crossSource); // 0.30% is under 0.25% x2
    }

    [Fact]
    public void A_move_exactly_at_the_threshold_fires()
    {
        var events = SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FiveMinutes, 0.25m)), Options());

        Assert.Single(events);
    }

    [Fact]
    public void A_move_below_the_threshold_does_not_fire()
    {
        var events = SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FiveMinutes, 0.2499m)), Options());

        Assert.Empty(events);
    }

    [Fact]
    public void Null_window_never_fires()
    {
        var events = SignificanceClassifier.Classify(Snapshot(), Options());

        Assert.Empty(events);
    }

    [Fact]
    public void Window_without_threshold_never_fires()
    {
        // 15m has no entry in Options(): switched off, however big the move.
        var events = SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FifteenMinutes, 5m)), Options());

        Assert.Empty(events);
    }

    [Fact]
    public void Down_move_fires_with_direction_down()
    {
        var events = SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FiveMinutes, -0.30m)), Options());

        var priceEvent = Assert.Single(events);
        Assert.Equal(PriceEventDirection.Down, priceEvent.Direction);
        Assert.Equal(-0.30m, priceEvent.DeltaPercent);
    }

    [Fact]
    public void Each_window_that_crosses_its_threshold_is_its_own_event()
    {
        var events = SignificanceClassifier.Classify(
            Snapshot(Delta(DeltaWindow.FiveMinutes, 0.30m), Delta(DeltaWindow.OneHour, 0.80m)), Options());

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.WindowCode == "5m");
        Assert.Contains(events, e => e.WindowCode == "1h");
    }

    [Fact]
    public void Event_copies_the_window_and_records_the_rule()
    {
        var delta = Delta(DeltaWindow.FiveMinutes, 0.60m, crossSource: true);

        var priceEvent = Assert.Single(SignificanceClassifier.Classify(Snapshot(delta), Options()));

        Assert.Equal(SupportedSymbol.Gold, priceEvent.Symbol);
        Assert.Equal(Now, priceEvent.DetectedAt);
        Assert.Equal(delta.Start.ObservedAt, priceEvent.WindowStartedAt);
        Assert.Equal(delta.End.ObservedAt, priceEvent.WindowEndedAt);
        Assert.Equal(PriceEventDirection.Up, priceEvent.Direction);
        Assert.Equal(delta.Start.Mid, priceEvent.StartMid);
        Assert.Equal(delta.End.Mid, priceEvent.EndMid);
        Assert.Equal(6, priceEvent.SampleCount);
        Assert.Equal(Primary, priceEvent.SourceCode);
        Assert.Equal(Backup, priceEvent.BaselineSourceCode);
        Assert.True(priceEvent.IsCrossSource);
        Assert.Equal("test-v1", priceEvent.ThresholdProfile);
        Assert.Equal("magnitude >= 0.50% (0.25% x2 cross-source)", priceEvent.TriggeredRule);
    }

    [Fact]
    public void Same_source_rule_names_only_the_threshold()
    {
        var priceEvent = Assert.Single(
            SignificanceClassifier.Classify(Snapshot(Delta(DeltaWindow.FiveMinutes, 0.30m)), Options()));

        Assert.Equal("magnitude >= 0.25%", priceEvent.TriggeredRule);
    }
}
