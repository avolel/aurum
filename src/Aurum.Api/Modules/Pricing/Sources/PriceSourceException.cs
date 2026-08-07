namespace Aurum.Api.Modules.Pricing.Sources;

/// <summary>The provider was reachable but its response was unusable.</summary>
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
