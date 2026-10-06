using System.Text;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>The provider was reachable but its response was unusable.</summary>
/// <remarks>
/// Thrown above the HTTP pipeline, so a Polly retry predicate can never see it. It is a failover
/// and circuit-fault signal, counted by <c>FailoverPriceFeed</c> (D-14).
/// </remarks>
public class PriceSourceException(string sourceCode, string message, Exception? inner = null)
    : Exception($"[{sourceCode}] {message}", inner)
{
    public string SourceCode { get; } = sourceCode;
}

/// <summary>
/// The source's budget for this period is spent. Not retryable and not a circuit fault.
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
    /// True only when every source was out of budget. One fault or open circuit makes it false,
    /// so the poller never sleeps to a reset over a problem that clears in minutes.
    /// </summary>
    public bool AllQuotaExhausted =>
        Attempts.Count > 0 && Attempts.All(a => a.Outcome == SourceAttemptOutcome.QuotaExhausted);

    public DateTimeOffset? EarliestResetsAt =>
        Attempts.Where(a => a.QuotaResetsAt.HasValue).Min(a => a.QuotaResetsAt);
}
