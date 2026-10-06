namespace Aurum.App.Infrastructure.Pricing.Quota;

/// <summary>
/// Request budget for a quota-limited upstream. Must survive a restart and be safe under
/// concurrent acquires; an in-memory counter can spend a month in an afternoon (D-7).
/// </summary>
public interface IQuotaGovernor
{
    /// <summary>
    /// Atomically consume one request from <paramref name="sourceCode"/>'s current period,
    /// creating the period's counter if this is the first call in it.
    /// </summary>
    /// <returns>
    /// A granted lease, or a denied one carrying the reset time. Running out does not throw.
    /// </returns>
    Task<QuotaLease> AcquireAsync(string sourceCode, CancellationToken ct);

    /// <summary>
    /// Record that the provider itself rejected us for quota (HTTP 429/402).
    /// </summary>
    /// <remarks>
    /// The provider is authoritative, so this clamps the rest of the period to zero.
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
/// true it is zero; <paramref name="Used"/> keeps the real count as evidence of drift (D-7).
/// </remarks>
public readonly record struct QuotaStatus(int Used, int Limit, DateTimeOffset ResetsAt, bool ProviderRejected);
