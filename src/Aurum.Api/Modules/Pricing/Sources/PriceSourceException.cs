namespace Aurum.Api.Modules.Pricing.Sources;

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
/// HTTP-level breaker — is the reason D-13 rejects Polly's breaker.
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
