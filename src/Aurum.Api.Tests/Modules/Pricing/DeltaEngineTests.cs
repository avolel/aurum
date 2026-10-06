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
/// The delta engine in memory. No container, no HTTP, sub-second.
/// </summary>
/// <remarks>
/// Every window is measured from the injected clock at read time, so moving a fake clock is the
/// whole setup. Warm-up against stored ticks needs Postgres and lives in
/// <c>DeltaEngineWarmupTests</c>.
/// </remarks>
public class DeltaEngineTests
{
    private const string Primary = "api-ninjas";
    private const string Backup = "goldapi.io";

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static (DeltaEngine Engine, FakeTimeProvider Clock) Build(
        int capacity = 8_640, IServiceScopeFactory? scopes = null)
    {
        var clock = new FakeTimeProvider(Now);

        var engine = new DeltaEngine(
            scopes ?? new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeltaEngineOptions { MaxSamplesPerSymbol = capacity }),
            clock,
            NullLogger<DeltaEngine>.Instance);

        return (engine, clock);
    }

    private static PriceQuote Quote(TimeSpan ago, decimal mid = 4_000m, string sourceCode = Primary) =>
        new(SupportedSymbol.Gold, Now - ago, Now - ago, null, null, mid, sourceCode);

    private static void RecordAll(DeltaEngine engine, params PriceQuote[] quotes)
    {
        foreach (var quote in quotes)
        {
            engine.Record(quote);
        }
    }

    private static DeltaSnapshot Snapshot(DeltaEngine engine) =>
        engine.GetSnapshot(SupportedSymbol.Gold) ?? throw new InvalidOperationException("No snapshot.");

    private static TimeSpan Minutes(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void A_symbol_never_recorded_has_no_snapshot()
    {
        var (engine, _) = Build();

        Assert.Null(engine.GetSnapshot(SupportedSymbol.Gold));
    }

    /// <summary>
    /// The spec's own example. Two prices four hours apart: "newest minus oldest in the window"
    /// would give 0.00% for 1m, 5m and 15m. That is "no data", not "no move".
    /// </summary>
    [Fact]
    public void Window_with_no_bracketing_sample_is_null_not_zero()
    {
        var (engine, _) = Build();
        RecordAll(engine, Quote(TimeSpan.FromHours(4), 4_000m), Quote(TimeSpan.Zero, 4_040m));

        var snapshot = Snapshot(engine);

        Assert.Null(snapshot[DeltaWindow.OneMinute]);
        Assert.Null(snapshot[DeltaWindow.FiveMinutes]);
        Assert.Null(snapshot[DeltaWindow.FifteenMinutes]);
        Assert.Null(snapshot[DeltaWindow.OneHour]);  // start is 3h before the window start
        Assert.Null(snapshot[DeltaWindow.OneDay]);   // every price is newer than the window start

        var fourHours = snapshot[DeltaWindow.FourHours];
        Assert.NotNull(fourHours);
        Assert.Equal(1m, fourHours.DeltaPercent);
    }

    [Fact]
    public void A_single_price_answers_no_window()
    {
        var (engine, _) = Build();
        engine.Record(Quote(TimeSpan.Zero));

        Assert.All(Snapshot(engine).Windows.Values, Assert.Null);
    }

    [Fact]
    public void Out_of_order_sample_is_dropped()
    {
        var (engine, _) = Build();
        RecordAll(engine, Quote(Minutes(65), 4_000m), Quote(TimeSpan.Zero, 4_040m));

        engine.Record(Quote(Minutes(30), 9_999m));            // older than the newest
        engine.Record(Quote(TimeSpan.Zero, 9_999m, Backup));  // same instant, different source

        var snapshot = Snapshot(engine);
        Assert.Equal(2, snapshot.DroppedOutOfOrder);

        // Neither dropped price is in the history: the move is still 4,000 -> 4,040 over two prices.
        var hour = snapshot[DeltaWindow.OneHour]!;
        Assert.Equal(40m, hour.DeltaAbsolute);
        Assert.Equal(2, hour.SampleCount);
        Assert.False(hour.CrossSource);
    }

    /// <summary>
    /// Pins "last at or before", not "first after". First-after would start the 1h window at the
    /// -55 minute price and still call it one hour.
    /// </summary>
    [Fact]
    public void Starting_price_is_the_last_at_or_before_the_window_start()
    {
        var (engine, _) = Build();
        RecordAll(engine,
            Quote(Minutes(65), 4_000m),
            Quote(Minutes(55), 4_010m),
            Quote(TimeSpan.Zero, 4_020m));

        var hour = Snapshot(engine)[DeltaWindow.OneHour]!;

        Assert.Equal(Now - Minutes(65), hour.Start.ObservedAt);
        Assert.Equal(20m, hour.DeltaAbsolute);
        Assert.Equal(0.5m, hour.DeltaPercent);
        Assert.Equal(3, hour.SampleCount);
    }

    [Fact]
    public void Velocity_is_divided_by_the_real_span_not_the_window_length()
    {
        var (engine, _) = Build();
        RecordAll(engine, Quote(Minutes(80), 4_000m), Quote(TimeSpan.Zero, 4_040m));

        var hour = Snapshot(engine)[DeltaWindow.OneHour]!;

        // 1% over the 80 minutes that actually passed, not over the window's 60.
        Assert.Equal(1m / 80m, hour.VelocityPercentPerMinute);
    }

    [Theory]
    [InlineData(90, true)]   // 30 minutes before the window start: exactly the 1h tolerance
    [InlineData(91, false)]  // one minute past it
    public void Gap_beyond_tolerance_is_null(int startMinutesAgo, bool answered)
    {
        var (engine, _) = Build();
        RecordAll(engine, Quote(Minutes(startMinutesAgo)), Quote(TimeSpan.Zero, 4_040m));

        Assert.Equal(answered, Snapshot(engine)[DeltaWindow.OneHour] is not null);
    }

    /// <summary>
    /// A start price is available at every instant (one sample a minute), so only the newest
    /// price's age decides. Without the freshness check the stale case still answers, and a move
    /// that ended half an hour ago is reported as the current 1h move.
    /// </summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Stale_newest_price_is_null(int minutesSinceNewest, bool answered)
    {
        var (engine, clock) = Build();
        for (var ago = 120; ago >= 0; ago--)
        {
            engine.Record(Quote(Minutes(ago), 4_000m + ago));
        }

        clock.Advance(Minutes(minutesSinceNewest));

        Assert.Equal(answered, Snapshot(engine)[DeltaWindow.OneHour] is not null);
    }

    [Theory]
    [InlineData(Primary, Backup, true)]
    [InlineData(Primary, Primary, false)]
    public void Mixed_sources_are_flagged_CrossSource(string startSource, string endSource, bool crossSource)
    {
        var (engine, _) = Build();
        RecordAll(engine,
            Quote(Minutes(60), 4_000m, startSource),
            Quote(Minutes(30), 4_010m, Backup),  // the middle price's source must not matter
            Quote(TimeSpan.Zero, 4_020m, endSource));

        Assert.Equal(crossSource, Snapshot(engine)[DeltaWindow.OneHour]!.CrossSource);
    }

    [Fact]
    public void Volatility_is_null_below_the_minimum_sample_count()
    {
        var (engine, _) = Build();
        RecordAll(engine,
            Quote(Minutes(60), 1_000m),
            Quote(Minutes(45), 1_100m),
            Quote(Minutes(30), 990m),
            Quote(Minutes(15), 1_089m));

        var four = Snapshot(engine)[DeltaWindow.OneHour]!;
        Assert.Equal(4, four.SampleCount);
        Assert.Null(four.Volatility);

        engine.Record(Quote(TimeSpan.Zero, 980.1m));

        // The fifth price: changes of +10, -10, +10, -10 percent. Mean 0, so the sample standard
        // deviation is sqrt(4 * 100 / 3), worked by hand.
        var five = Snapshot(engine)[DeltaWindow.OneHour]!;
        Assert.Equal(5, five.SampleCount);
        Assert.NotNull(five.Volatility);
        Assert.Equal(11.547005383792516, five.Volatility.Value, precision: 9);
    }

    /// <summary>
    /// Through the engine, at the plan's capacity of 4 with 10 prices: the start position has
    /// wrapped, and the windows that reach past what is held must say no answer.
    /// </summary>
    [Fact]
    public void Buffer_wraps_and_search_still_finds_the_start()
    {
        var (engine, _) = Build(capacity: 4);
        for (var ago = 9; ago >= 0; ago--)
        {
            engine.Record(Quote(Minutes(ago), 4_000m + (9 - ago)));
        }

        var snapshot = Snapshot(engine);

        // Held: -3, -2, -1, 0 minutes. The 1m window starts at the -1 price.
        var minute = snapshot[DeltaWindow.OneMinute]!;
        Assert.Equal(Now - Minutes(1), minute.Start.ObservedAt);
        Assert.Equal(1m, minute.DeltaAbsolute);

        // The -5 price was overwritten, so the 5m window has nothing at or before its start.
        Assert.Null(snapshot[DeltaWindow.FiveMinutes]);
    }

    [Fact]
    public async Task A_failed_warm_up_is_retried_by_the_next_caller()
    {
        // An empty container: resolving AurumDbContext throws, so every load fails.
        var scopes = new CountingScopeFactory(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var (engine, _) = Build(scopes: scopes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnsureWarmAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnsureWarmAsync(CancellationToken.None));

        // Two loads, not one cached failure handed to the second caller.
        Assert.Equal(2, scopes.Created);
    }
}
