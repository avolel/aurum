namespace Aurum.Api.Modules.Pricing.Quota;

/// <summary>
/// Durable request budget for a rate- or quota-limited upstream.
/// </summary>
/// <remarks>
/// The property that matters: budget accounting must survive process restart. GoldAPI's free
/// tier resets monthly, so an in-memory counter that resets with the container can spend a
/// month of requests in an afternoon. Implementations must therefore be durable and safe
/// against concurrent acquires from multiple callers.
/// </remarks>
public interface IQuotaGovernor
{
    /// <summary>
    /// Atomically consume one request from <paramref name="sourceCode"/>'s current period,
    /// creating the period's counter if this is the first call in it.
    /// </summary>
    /// <returns>
    /// A granted lease if budget remained, otherwise a denied one carrying the reset time.
    /// Never throws for the ordinary "out of budget" case — that is a normal control-flow
    /// outcome the failover chain acts on.
    /// </returns>
    Task<QuotaLease> AcquireAsync(string sourceCode, CancellationToken ct);

    /// <summary>
    /// Record that the provider itself rejected us for quota (HTTP 429/402).
    /// </summary>
    /// <remarks>
    /// The provider is authoritative. Our local count can only ever be an under-count — a
    /// request that left the process but was not recorded — so this must clamp the period's
    /// remaining budget to zero rather than merely incrementing the counter.
    /// </remarks>
    Task ReportProviderRejectionAsync(string sourceCode, CancellationToken ct);

    /// <summary>Read-only budget snapshot, for /health/ready and operator visibility.</summary>
    Task<QuotaStatus> GetStatusAsync(string sourceCode, CancellationToken ct);
}

/// <param name="Granted">Whether the caller may issue the request.</param>
/// <param name="Remaining">Budget left after this acquire; 0 when denied.</param>
/// <param name="ResetsAt">When the current period ends and budget returns.</param>
public readonly record struct QuotaLease(bool Granted, int Remaining, DateTimeOffset ResetsAt);

/// <param name="Used">Requests consumed in the current period.</param>
/// <param name="Limit">Requests allowed in the current period.</param>
/// <param name="ResetsAt">When the current period ends.</param>
/// <param name="ProviderRejected">Whether the provider has rejected us for quota this period.</param>
/// <remarks>
/// <b>Remaining budget is not <c>Limit - Used</c>.</b> When <paramref name="ProviderRejected"/> is
/// true the remaining budget is zero regardless of the counter: a rejection records that the
/// provider's count disagrees with ours, and deliberately leaves <paramref name="Used"/> at what we
/// actually observed. That gap is the only evidence we get that our accounting is drifting, so it is
/// preserved rather than overwritten.
/// </remarks>
public readonly record struct QuotaStatus(int Used, int Limit, DateTimeOffset ResetsAt, bool ProviderRejected);
