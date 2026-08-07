using Aurum.Api.Shared;

namespace Aurum.Api.Modules.Pricing.Quota;

/// <summary>
/// STUB — not implemented. This is the one piece of Phase 0 left to write by hand.
/// </summary>
/// <remarks>
/// <para>
/// Intended design (option A from the Phase 0 review): Postgres <em>is</em> the bucket. Acquire
/// is a single conditional UPDATE against <see cref="ApiQuotaWindow"/>, so atomicity and
/// durability come from the database rather than from application locking:
/// </para>
/// <code>
/// UPDATE api_quota_windows
///    SET requests_used = requests_used + 1, updated_at = now()
///  WHERE source_code = @code AND period_key = @period
///    AND requests_used &lt; request_limit
///    AND provider_rejected_at IS NULL
/// RETURNING request_limit - requests_used AS remaining, period_ends_at;
/// </code>
/// <para>Zero rows affected means one of three things, which the implementation must tell apart:
/// the period row does not exist yet (create it and retry once), budget is spent (deny), or the
/// provider already rejected us this period (deny).</para>
///
/// <para><b>Contract the implementation must satisfy:</b></para>
/// <list type="number">
/// <item>Budget survives process restart — a fresh instance sees the used count, not zero.
/// This is the property the failing test asserts.</item>
/// <item>Concurrent acquires never oversubscribe: N racing callers against a budget of M grant
/// exactly min(N, M) leases. No two callers may create competing rows for the same period —
/// the unique index on (source_code, period_key) is what makes that impossible.</item>
/// <item>Period rollover is derived from <see cref="QuotaPeriodKind"/>, computed in UTC, and
/// must not require a scheduled job — the period key changing is what rolls the budget.</item>
/// <item><see cref="ReportProviderRejectionAsync"/> clamps remaining budget to zero for the
/// rest of the period.</item>
/// </list>
///
/// <para><b>Decisions deliberately left open:</b></para>
/// <list type="bullet">
/// <item>Refunds. If the HTTP call fails with a transport error after the lease was granted, we
/// cannot know whether the provider counted it. Refunding risks overspending a monthly budget;
/// not refunding leaks budget on every network blip. Pick one and write down why.</item>
/// <item>Whether a denied acquire is logged per-call or rate-limited — at a 5-minute poll a
/// spent monthly budget means thousands of identical denials.</item>
/// <item>Clock source. <see cref="TimeProvider"/> is injected so tests can roll periods, but
/// period boundaries computed from the app clock and enforced against a row written under the
/// database clock can disagree. Decide which one is authoritative.</item>
/// </list>
/// </remarks>
public class PostgresQuotaGovernor(
    AurumDbContext db,
    TimeProvider clock,
    ILogger<PostgresQuotaGovernor> logger) : IQuotaGovernor
{
    public Task<QuotaLease> AcquireAsync(string sourceCode, CancellationToken ct)
        => throw new NotImplementedException("Phase 0: implement the atomic conditional UPDATE described in the class remarks.");

    public Task ReportProviderRejectionAsync(string sourceCode, CancellationToken ct)
        => throw new NotImplementedException("Phase 0: clamp the current period's remaining budget to zero.");

    public Task<QuotaStatus> GetStatusAsync(string sourceCode, CancellationToken ct)
        => throw new NotImplementedException("Phase 0: read the current period row, or report a full budget if none exists yet.");

    /// <summary>
    /// Maps a moment to the provider's accounting period. Opaque to callers; only equality and
    /// the period's end instant matter.
    /// </summary>
    internal static (string PeriodKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt) ResolvePeriod(
        QuotaPeriodKind kind,
        DateTimeOffset now,
        DateTimeOffset? anchor)
        => throw new NotImplementedException("Phase 0: CalendarMonthUtc and RollingThirtyDays.");
}
