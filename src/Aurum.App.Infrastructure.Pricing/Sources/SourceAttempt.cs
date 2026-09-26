namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <param name="FailureReason">
/// Operator-facing, projected into price_sources.LastFailureReason (max 512). Never formatted
/// from a request URI: MetalpriceAPI authenticates by query parameter, and this string is
/// persisted and, from item 8, served over HTTP.
/// </param>
/// <param name="Exception">
/// Logging only. Item 4's cache must project to its own record rather than retain this one, or
/// every cached quote pins a stack trace and whatever the transport hung off it.
/// </param>
public sealed record SourceAttempt(
    string SourceCode, SourceAttemptOutcome Outcome,
    string? FailureReason = null, Exception? Exception = null, DateTimeOffset? QuotaResetsAt = null);
