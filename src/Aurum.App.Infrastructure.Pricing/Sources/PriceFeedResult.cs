namespace Aurum.App.Infrastructure.Pricing.Sources;

public sealed record PriceFeedResult(
    PriceQuote Quote, string PrimarySourceCode, IReadOnlyList<SourceAttempt> Attempts)
{
    public bool UsedFallback =>
        !string.Equals(Quote.SourceCode, PrimarySourceCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sources actually called, in order. Excludes circuit-skipped ones.</summary>
    /// <remarks>Item 4's cache reports what a poll spent, not what it considered.</remarks>
    public IReadOnlyList<string> AttemptedSources =>
        [.. Attempts.Where(a => a.Outcome != SourceAttemptOutcome.SkippedCircuitOpen)
                    .Select(a => a.SourceCode)];
}
