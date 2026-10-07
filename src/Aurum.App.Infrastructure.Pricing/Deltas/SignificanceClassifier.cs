using System.Globalization;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// Decides which of a snapshot's windows moved enough to be an event (BR-02).
/// </summary>
/// <remarks>
/// Pure: no clock, no database, no waiting period. The waiting period is checked when the event is
/// saved, in the database, so it survives a restart (D-18).
/// </remarks>
public static class SignificanceClassifier
{
    public static IReadOnlyList<PriceEvent> Classify(DeltaSnapshot snapshot, SignificanceOptions options)
    {
        var events = new List<PriceEvent>();

        foreach (var (window, delta) in snapshot.Windows)
        {
            // A null window is "no answer", never 0%; a window missing from the map is switched off.
            if (delta is null || !options.Windows.TryGetValue(window.Code, out var threshold))
            {
                continue;
            }

            var required = delta.CrossSource
                ? threshold.MinPercent * options.CrossSourceMagnitudeMultiplier
                : threshold.MinPercent;

            if (Math.Abs(delta.DeltaPercent) < required)
            {
                continue;
            }

            events.Add(new PriceEvent
            {
                Symbol = snapshot.Symbol,
                DetectedAt = snapshot.AsOf,
                WindowCode = window.Code,
                WindowStartedAt = delta.Start.ObservedAt,
                WindowEndedAt = delta.End.ObservedAt,
                Direction = delta.DeltaPercent >= 0 ? PriceEventDirection.Up : PriceEventDirection.Down,
                StartMid = delta.Start.Mid,
                EndMid = delta.End.Mid,
                DeltaAbsolute = delta.DeltaAbsolute,
                DeltaPercent = delta.DeltaPercent,
                VelocityPercentPerMinute = delta.VelocityPercentPerMinute,
                Volatility = (decimal?)delta.Volatility,
                SampleCount = delta.SampleCount,
                SourceCode = delta.EndSourceCode,
                BaselineSourceCode = delta.StartSourceCode,
                IsCrossSource = delta.CrossSource,
                ThresholdProfile = options.ThresholdProfile,
                TriggeredRule = Rule(required, threshold.MinPercent, options.CrossSourceMagnitudeMultiplier, delta.CrossSource),
            });
        }

        return events;
    }

    // e.g. "magnitude >= 0.50% (0.25% x2 cross-source)". Invariant culture so a server locale
    // cannot write "0,50%" into a row.
    private static string Rule(decimal required, decimal minPercent, decimal multiplier, bool crossSource) =>
        crossSource
            ? string.Create(CultureInfo.InvariantCulture, $"magnitude >= {required:0.00##}% ({minPercent:0.00##}% x{multiplier:0.##} cross-source)")
            : string.Create(CultureInfo.InvariantCulture, $"magnitude >= {required:0.00##}%");
}
