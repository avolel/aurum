using System.Collections.Concurrent;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <remarks>
/// One <see cref="TickRingBuffer"/> per symbol, behind its own lock, serving all six windows by
/// binary search (D-17). Singleton with the same single-instance assumption as the poller.
/// </remarks>
internal sealed class DeltaEngine(
    IServiceScopeFactory scopeFactory,
    IOptions<DeltaEngineOptions> options,
    TimeProvider clock,
    ILogger<DeltaEngine> logger) : IDeltaEngine
{
    private readonly DeltaEngineOptions _options = options.Value;

    private readonly ConcurrentDictionary<string, SymbolHistory> _histories =
        new(StringComparer.OrdinalIgnoreCase);

    // Handed out on first sight of each source code. Only equality between two ordinals means
    // anything. checked: a 257th source throws rather than wrapping onto an existing ordinal.
    private readonly ConcurrentDictionary<string, byte> _ordinals = new(StringComparer.OrdinalIgnoreCase);
    private int _lastOrdinal = -1;

    // The reverse of _ordinals. Written before the sample reaches a buffer, so any ordinal read
    // back under a symbol's lock already has its code here.
    private readonly string[] _codes = new string[byte.MaxValue + 1];

    private readonly SemaphoreSlim _warmGate = new(1, 1);
    private bool _warmed;

    public void Record(PriceQuote quote)
    {
        var history = History(quote.Symbol);
        var sample = new Sample(quote.ObservedAt, quote.Mid, Ordinal(quote.SourceCode));

        lock (history.Gate)
        {
            if (history.Buffer.TryAppend(sample))
            {
                return;
            }

            // Counted here, not in TryAppend, because warm-up overlap is not a fault.
            history.DroppedOutOfOrder++;
        }

        logger.LogDebug(
            "Dropped {Symbol} price from {SourceCode} observed at {ObservedAt:o}: not newer than the newest held price.",
            quote.Symbol, quote.SourceCode, quote.ObservedAt);
    }

    public DeltaSnapshot? GetSnapshot(string symbol)
    {
        if (!_histories.TryGetValue(symbol, out var history))
        {
            return null;
        }

        // The injected clock, read once, never the newest price's time: otherwise an hour that ended
        // eight hours ago would be labelled "the 1h move" (D-17).
        var now = clock.GetUtcNow();

        lock (history.Gate)
        {
            var windows = DeltaWindow.All.ToDictionary(w => w, w => Measure(history.Buffer, w, now));
            return new DeltaSnapshot(symbol, now, windows, history.DroppedOutOfOrder);
        }
    }

    /// <summary>One window's move, or null. Called under the symbol's lock.</summary>
    private WindowDelta? Measure(TickRingBuffer buffer, DeltaWindow window, DateTimeOffset now)
    {
        var tolerance = _options.Tolerance(window);
        var target = now - window.Length;

        var endIndex = buffer.Count - 1;
        var startIndex = buffer.LastIndexAtOrBefore(target);

        // -1: nothing at or before the window start. Equal to endIndex: only one price to use.
        if (startIndex < 0 || startIndex == endIndex)
        {
            return null;
        }

        var start = buffer[startIndex];
        var end = buffer[endIndex];

        // Strictly greater: a gap of exactly the tolerance is still an answer. A zero start mid is
        // possible for an unbanded symbol, and a percent change from zero is not a move.
        if (target - start.ObservedAt > tolerance || now - end.ObservedAt > tolerance || start.Mid <= 0)
        {
            return null;
        }

        var deltaAbsolute = end.Mid - start.Mid;
        var deltaPercent = deltaAbsolute / start.Mid * 100m;
        var minutes = (decimal)(end.ObservedAt - start.ObservedAt).TotalMinutes;
        var sampleCount = endIndex - startIndex + 1;

        return new WindowDelta(
            window,
            start,
            end,
            deltaAbsolute,
            deltaPercent,
            VelocityPercentPerMinute: deltaPercent / minutes,
            Volatility: sampleCount < _options.MinSamplesForVolatility
                ? null
                : Volatility(buffer, startIndex, endIndex),
            sampleCount,
            CrossSource: start.SourceOrdinal != end.SourceOrdinal,
            StartSourceCode: _codes[start.SourceOrdinal],
            EndSourceCode: _codes[end.SourceOrdinal]);
    }

    /// <summary>
    /// Sample standard deviation of the percent change between each pair of neighbouring prices.
    /// </summary>
    private static double? Volatility(TickRingBuffer buffer, int startIndex, int endIndex)
    {
        var changes = new List<double>(endIndex - startIndex);
        for (var i = startIndex + 1; i <= endIndex; i++)
        {
            var previous = buffer[i - 1].Mid;
            if (previous <= 0)
            {
                return null;
            }

            changes.Add((double)((buffer[i].Mid - previous) / previous * 100m));
        }

        // MinSamplesForVolatility is at least 3, so there are always at least two changes.
        var mean = changes.Average();
        var variance = changes.Sum(c => (c - mean) * (c - mean)) / (changes.Count - 1);
        return Math.Sqrt(variance);
    }

    /// <remarks>
    /// Loads at most once. A second load would add nothing: every stored row is older than the
    /// newest live price, and the buffer never re-sorts. A failed or cancelled load leaves
    /// <c>_warmed</c> false, so the next caller tries again.
    /// </remarks>
    public async Task EnsureWarmAsync(CancellationToken ct)
    {
        await _warmGate.WaitAsync(ct);
        try
        {
            if (!_warmed)
            {
                await WarmAsync(ct);
                _warmed = true;
            }
        }
        finally
        {
            _warmGate.Release();
        }
    }

    private async Task WarmAsync(CancellationToken ct)
    {
        // A scope per load, never a held DbContext: this object lives for the whole process.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();

        // Lookback, not one day: see DeltaEngineOptions.Lookback.
        var since = clock.GetUtcNow() - _options.Lookback;

        foreach (var symbol in SupportedSymbol.All)
        {
            // Newest first so the LIMIT keeps the newest rows; flipped into time order below.
            var rows = await db.PriceTicks.AsNoTracking()
                .Where(t => t.Symbol == symbol && t.ObservedAt >= since)
                .OrderByDescending(t => t.ObservedAt)
                .Take(_options.MaxSamplesPerSymbol)
                .Select(t => new { t.ObservedAt, t.Mid, t.SourceCode })
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                // No history created, so GetSnapshot stays null ("never held a price").
                logger.LogInformation(
                    "No stored {Symbol} ticks since {Since:o} to warm the price history from.", symbol, since);
                continue;
            }

            rows.Reverse();

            var history = History(symbol);
            var loaded = 0;
            lock (history.Gate)
            {
                foreach (var row in rows)
                {
                    // Refusals are overlap with live prices or a shared ObservedAt, not faults.
                    if (history.Buffer.TryAppend(new Sample(row.ObservedAt, row.Mid, Ordinal(row.SourceCode))))
                    {
                        loaded++;
                    }
                }
            }

            logger.LogInformation(
                "Warmed {Symbol} price history: {Loaded} of {Stored} stored ticks since {Since:o}.",
                symbol, loaded, rows.Count, since);
        }
    }

    private SymbolHistory History(string symbol) =>
        _histories.GetOrAdd(symbol, _ => new SymbolHistory(new TickRingBuffer(_options.MaxSamplesPerSymbol)));

    // The factory can run more than once under contention; a losing run fills a slot no sample uses.
    private byte Ordinal(string sourceCode) =>
        _ordinals.GetOrAdd(sourceCode, code =>
        {
            var ordinal = checked((byte)Interlocked.Increment(ref _lastOrdinal));
            _codes[ordinal] = code;
            return ordinal;
        });

    private sealed class SymbolHistory(TickRingBuffer buffer)
    {
        public Lock Gate { get; } = new();

        public TickRingBuffer Buffer { get; } = buffer;

        public long DroppedOutOfOrder { get; set; }
    }
}
