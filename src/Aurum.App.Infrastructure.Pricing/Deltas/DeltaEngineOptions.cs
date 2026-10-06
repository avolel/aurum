using System.ComponentModel.DataAnnotations;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>Bound from the <c>DeltaEngine</c> configuration section.</summary>
public class DeltaEngineOptions
{
    public const string SectionName = "DeltaEngine";

    /// <summary>
    /// Ring buffer capacity per symbol, about 340 KB at the default. The upper bound stops a typo
    /// allocating gigabytes; whether it is large enough is <see cref="BufferCoversLongestWindow"/>.
    /// </summary>
    [Range(2, 1_000_000)]
    public int MaxSamplesPerSymbol { get; set; } = 8_640;

    /// <summary>
    /// How far a window's prices may sit from the ideal, as a fraction of its length. Capped at 1 so
    /// a "1h move" can never be measured from a price more than two hours back (D-17).
    /// </summary>
    [Range(0.01, 1.0)]
    public double ToleranceFraction { get; set; } = 0.5;

    /// <summary>
    /// Fewest prices in a window before volatility is reported. At least 3, because a sample
    /// standard deviation needs two changes.
    /// </summary>
    [Range(3, int.MaxValue)]
    public int MinSamplesForVolatility { get; set; } = 5;

    public TimeSpan Tolerance(DeltaWindow window) => window.Length * ToleranceFraction;

    /// <summary>
    /// How far back the history must reach: the longest window plus its tolerance. The 1d starting
    /// price is older than "now minus one day", so stopping at one day loses exactly that price.
    /// </summary>
    public TimeSpan Lookback => DeltaWindow.Longest.Length + Tolerance(DeltaWindow.Longest);

    /// <summary>One sample per interval across <see cref="Lookback"/>, plus the newest.</summary>
    internal int RequiredSamples(TimeSpan pollInterval) =>
        (int)Math.Ceiling(Lookback / pollInterval) + 1;

    /// <summary>
    /// The boot-time check that the buffer can answer the 1d window at this poll interval. A
    /// non-positive interval passes here because the <c>PricePolling</c> validator refuses it.
    /// </summary>
    internal static bool BufferCoversLongestWindow(DeltaEngineOptions options, TimeSpan pollInterval) =>
        pollInterval <= TimeSpan.Zero || options.MaxSamplesPerSymbol >= options.RequiredSamples(pollInterval);
}
