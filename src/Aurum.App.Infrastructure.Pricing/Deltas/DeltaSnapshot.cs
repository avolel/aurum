namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// The price moves for one symbol, worked out at <see cref="AsOf"/>.
/// </summary>
/// <param name="Windows">
/// One entry per <see cref="DeltaWindow.All"/>. A null value is <b>no answer</b>: the app did not
/// hold two real prices spaced close enough to that window. It is never a zero-filled record, and
/// nothing downstream may show it as 0.00% (D-17).
/// </param>
/// <param name="DroppedOutOfOrder">
/// Live prices refused because they were not newer than the newest held one, since this process
/// started. From outside, "the price stopped moving" and "the app is dropping every price" look the
/// same; this counter is what tells them apart.
/// </param>
public sealed record DeltaSnapshot(
    string Symbol,
    DateTimeOffset AsOf,
    IReadOnlyDictionary<DeltaWindow, WindowDelta?> Windows,
    long DroppedOutOfOrder)
{
    public WindowDelta? this[DeltaWindow window] => Windows[window];
}

/// <summary>
/// One window's move, measured between two real prices.
/// </summary>
/// <param name="Start">The last price at or before <c>AsOf - Window.Length</c>.</param>
/// <param name="End">The newest price held.</param>
/// <param name="DeltaPercent">Percent change from <paramref name="Start"/> to <paramref name="End"/>.</param>
/// <param name="VelocityPercentPerMinute">
/// <paramref name="DeltaPercent"/> divided by the real minutes between the two prices, not by the
/// window's nominal length. The two differ by up to the tolerance at each end.
/// </param>
/// <param name="Volatility">
/// Sample standard deviation of the step-to-step percent changes from <paramref name="Start"/> to
/// <paramref name="End"/>. Null below <see cref="DeltaEngineOptions.MinSamplesForVolatility"/>. A
/// <see cref="double"/> because <see cref="decimal"/> has no square root. Not scaled for uneven spacing.
/// </param>
/// <param name="SampleCount">Prices from <paramref name="Start"/> to <paramref name="End"/>, both included.</param>
/// <param name="CrossSource">
/// <paramref name="Start"/> and <paramref name="End"/> came from different sources, so part of the
/// move may be the gap between two providers' prices rather than the market. A flag, not a fix.
/// </param>
public sealed record WindowDelta(
    DeltaWindow Window,
    Sample Start,
    Sample End,
    decimal DeltaAbsolute,
    decimal DeltaPercent,
    decimal VelocityPercentPerMinute,
    double? Volatility,
    int SampleCount,
    bool CrossSource);
