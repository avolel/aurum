using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// Seven hours of hand-written prices through the delta engine and the classifier, checked against
/// the exact events each stretch should produce. No container, sub-second.
/// </summary>
/// <remarks>
/// What each stretch is for is written next to it in <c>DeltaEngineReplay.csv</c>. The classifier
/// has no waiting period, so one move fires on every poll that still sees it; the waiting period is
/// the insert's job and is tested in <c>PriceEventSqlTests</c>.
/// </remarks>
public class DeltaEngineReplayTests
{
    private const uint Seed = 20261015;
    private const decimal Noise = 0.20m;

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The test's own thresholds, not the shipped ones: five of those are placeholders and will be
    /// tuned. 15m is 0.50%, not the shipped 0.40%, so the 0.4% jump does not land on it exactly.
    /// </summary>
    private static SignificanceOptions Options()
    {
        var options = new SignificanceOptions { ThresholdProfile = "replay-v1", CrossSourceMagnitudeMultiplier = 2m };
        options.Windows["1m"] = Threshold(0.15m);
        options.Windows["5m"] = Threshold(0.25m);
        options.Windows["15m"] = Threshold(0.50m);
        options.Windows["1h"] = Threshold(0.75m);
        options.Windows["4h"] = Threshold(1.25m);
        options.Windows["1d"] = Threshold(2.00m);
        return options;

        static SignificanceWindowOptions Threshold(decimal minPercent) =>
            new() { MinPercent = minPercent, Cooldown = TimeSpan.FromMinutes(5) };
    }

    private static IReadOnlyList<ReplayStep> Replay()
    {
        var clock = new FakeTimeProvider(Start);
        var engine = new DeltaEngine(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new DeltaEngineOptions()),
            clock,
            NullLogger<DeltaEngine>.Instance);

        var path = Path.Combine(AppContext.BaseDirectory, "Modules", "Pricing", "DeltaEngineReplay.csv");
        return TickReplay.Run(TickReplay.Load(path, Start, Seed, Noise), engine, clock, Options());
    }

    private static ReplayStep At(IReadOnlyList<ReplayStep> steps, int minute) =>
        steps.Single(s => s.Quote.ObservedAt == Start.AddMinutes(minute));

    [Fact]
    public void Replay_produces_exactly_the_expected_events()
    {
        var events = Replay().SelectMany(s => s.Events)
            .Select(e => (e.WindowCode, Minute: (int)(e.WindowEndedAt - Start).TotalMinutes, e.Direction, e.IsCrossSource));

        // Only the jump: 5m at the four minutes whose 5-minute rise is at least 0.25%.
        (string, int, string, bool)[] expected =
        [
            ("5m", 33, PriceEventDirection.Up, false),
            ("5m", 34, PriceEventDirection.Up, false),
            ("5m", 35, PriceEventDirection.Up, false),
            ("5m", 36, PriceEventDirection.Up, false),
        ];

        Assert.Equal(expected, events);
    }

    /// <summary>
    /// The first price after the gap. A 0.3% step across the gap would fire 1m and 5m if either were
    /// measured from the pre-gap price; the empty event list above only means something if these are null.
    /// </summary>
    [Fact]
    public void After_a_gap_every_short_window_gives_no_answer()
    {
        var snapshot = At(Replay(), 330).Snapshot;

        Assert.Null(snapshot[DeltaWindow.OneMinute]);
        Assert.Null(snapshot[DeltaWindow.FiveMinutes]);
        Assert.Null(snapshot[DeltaWindow.FifteenMinutes]);
        Assert.NotNull(snapshot[DeltaWindow.OneHour]); // reaches back past the gap, within tolerance
    }

    /// <summary>
    /// The switch is a real 0.30% 5-minute move by the numbers, over the 0.25% single-source
    /// threshold. Only the cross-source multiplier keeps it from being an event.
    /// </summary>
    [Fact]
    public void A_switch_between_services_moves_enough_to_fire_but_does_not()
    {
        var step = At(Replay(), 395);
        var fiveMinutes = step.Snapshot[DeltaWindow.FiveMinutes]!;

        Assert.True(fiveMinutes.CrossSource);
        Assert.Equal(ApiNinjasSource.SourceCode, fiveMinutes.StartSourceCode);
        Assert.Equal(GoldApiIoSource.SourceCode, fiveMinutes.EndSourceCode);
        Assert.InRange(fiveMinutes.DeltaPercent, 0.25m, 0.50m);
        Assert.Empty(step.Events);
    }

    [Fact]
    public void Same_seed_gives_the_same_prices()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Modules", "Pricing", "DeltaEngineReplay.csv");

        Assert.Equal(TickReplay.Load(path, Start, Seed, Noise), TickReplay.Load(path, Start, Seed, Noise));
    }
}
