namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <param name="FailureReason">
/// Saved to price_sources.LastFailureReason (max 512). Never built from a request URI: MetalpriceAPI's
/// key is in the query string.
/// </param>
/// <param name="Exception">Logging only. Never hold it in a cache (D-16).</param>
public sealed record SourceAttempt(
    string SourceCode, SourceAttemptOutcome Outcome,
    string? FailureReason = null, Exception? Exception = null, DateTimeOffset? QuotaResetsAt = null);
