using System.Globalization;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>One replayed price, the snapshot read straight after it, and what the classifier made of it.</summary>
internal sealed record ReplayStep(PriceQuote Quote, DeltaSnapshot Snapshot, IReadOnlyList<PriceEvent> Events);

/// <summary>
/// Turns a hand-written CSV of price stretches into one gold price per minute, and feeds them
/// through the delta engine and the classifier on a fake clock.
/// </summary>
/// <remarks>
/// Each CSV row is <c>fromMinute,toMinute,fromMid,toMid,source</c>: a straight line between the two
/// mids, one price per minute. Minutes no row covers are a gap. <c>#</c> lines are notes. Noise comes
/// from mulberry32, the same seeded generator as <c>app/src/fixture/rng.ts</c>, so a given seed
/// always gives the same prices.
/// </remarks>
internal static class TickReplay
{
    public static IReadOnlyList<PriceQuote> Load(string path, DateTimeOffset start, uint seed, decimal noise)
    {
        var next = Mulberry32(seed);
        var quotes = new List<PriceQuote>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            var fromMinute = int.Parse(cells[0], CultureInfo.InvariantCulture);
            var toMinute = int.Parse(cells[1], CultureInfo.InvariantCulture);
            var fromMid = decimal.Parse(cells[2], CultureInfo.InvariantCulture);
            var toMid = decimal.Parse(cells[3], CultureInfo.InvariantCulture);
            var source = cells[4];

            for (var minute = fromMinute; minute <= toMinute; minute++)
            {
                var progress = toMinute == fromMinute ? 0m : (decimal)(minute - fromMinute) / (toMinute - fromMinute);

                // Uniform in [-noise, +noise], to the cent.
                var jitter = Math.Round(((decimal)next() * 2 - 1) * noise, 2);
                var mid = Math.Round(fromMid + (toMid - fromMid) * progress + jitter, 4);
                var at = start.AddMinutes(minute);

                quotes.Add(new PriceQuote(SupportedSymbol.Gold, at, at, null, null, mid, source));
            }
        }

        return quotes;
    }

    /// <summary>Moves the clock to each price, records it, then snapshots and classifies, as a poll would.</summary>
    public static IReadOnlyList<ReplayStep> Run(
        IEnumerable<PriceQuote> quotes, IDeltaEngine engine, FakeTimeProvider clock, SignificanceOptions options)
    {
        var steps = new List<ReplayStep>();

        foreach (var quote in quotes)
        {
            clock.SetUtcNow(quote.ObservedAt);
            engine.Record(quote);

            var snapshot = engine.GetSnapshot(quote.Symbol)!;
            steps.Add(new ReplayStep(quote, snapshot, SignificanceClassifier.Classify(snapshot, options)));
        }

        return steps;
    }

    // A port of rng.ts's mulberry32. uint arithmetic wraps like Math.imul and >>> 0.
    private static Func<double> Mulberry32(uint seed)
    {
        var a = seed;
        return () =>
        {
            a += 0x6D2B79F5;
            var t = a;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return (t ^ (t >> 14)) / 4294967296.0;
        };
    }
}
