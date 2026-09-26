using System.Text;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>The provider was reachable but its response was unusable.</summary>
/// <remarks>
/// Never add this type to a Polly retry <c>ShouldHandle</c> predicate: the predicate cannot
/// observe it. Every source parses the body above the handler chain — a bad status, bad JSON
/// or a non-positive price is raised after <c>HttpClient.SendAsync</c> has returned an outcome
/// the pipeline already judged successful and unwound. A retryable transport failure reaches
/// the pipeline as an <c>HttpRequestException</c>, a timeout, or a failing status code, and is
/// handled on those; by the time this is thrown the retries are spent.
///
/// This type is a failover-and-circuit-fault signal only, and the fault is counted by the
/// hand-rolled breaker in <c>FailoverPriceFeed</c>, which sits above the sources and does see
/// it. That gap — a degrading provider returning 200s full of junk being invisible to an
/// HTTP-level breaker — is the reason D-14 rejects Polly's breaker.
/// </remarks>
public class PriceSourceException(string sourceCode, string message, Exception? inner = null)
    : Exception($"[{sourceCode}] {message}", inner)
{
    public string SourceCode { get; } = sourceCode;
}

/// <summary>
/// The source's request budget for the current accounting period is spent. Distinct from
/// <see cref="PriceSourceException"/> because it is not retryable within the period: the
/// failover chain must move on, and a circuit breaker should not count it as a fault.
/// </summary>
public class QuotaExhaustedException(string sourceCode, DateTimeOffset resetsAt)
    : Exception($"[{sourceCode}] request quota exhausted until {resetsAt:O}.")
{
    public string SourceCode { get; } = sourceCode;

    public DateTimeOffset ResetsAt { get; } = resetsAt;
}

public sealed class AllSourcesFailedException(string symbol, IReadOnlyList<SourceAttempt> attempts)
    : Exception(BuildMessage(symbol, attempts),
                attempts.FirstOrDefault(a => a.Outcome == SourceAttemptOutcome.Faulted)?.Exception)
{
    public string Symbol { get; } = symbol;
    public IReadOnlyList<SourceAttempt> Attempts { get; } = attempts;

    private static string BuildMessage(string symbol, IReadOnlyList<SourceAttempt> attempts)
    {
        var sb = new StringBuilder($"All {attempts.Count} sources failed for {symbol}:");
        foreach (var attempt in attempts)
        {
            sb.AppendLine();
            sb.Append($"  [{attempt.SourceCode}] {attempt.Outcome}");
            if (attempt.Exception is not null)
            {
                sb.Append($": {attempt.Exception.Message}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// True only when every source was tried and every one was out of budget. A single open
    /// circuit or a single fault makes this false: those clear on their own schedule, and the
    /// poller must not sleep a month waiting for a quota period that was never the problem.
    /// </summary>
    public bool AllQuotaExhausted =>
        Attempts.Count > 0 && Attempts.All(a => a.Outcome == SourceAttemptOutcome.QuotaExhausted);

    public DateTimeOffset? EarliestResetsAt =>
        Attempts.Where(a => a.QuotaResetsAt.HasValue).Min(a => a.QuotaResetsAt);
}
