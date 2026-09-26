using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Pricing.Quota;

/// <summary>
/// Durable per-source, per-period request counter. One row per
/// (<see cref="SourceCode"/>, <see cref="PeriodKey"/>).
/// </summary>
/// <remarks>
/// This table presupposes governor design A (Postgres *is* the bucket: acquire is a single
/// conditional UPDATE). If you decide instead to derive usage from an append-only call log,
/// replace this entity before the first migration is ever applied — afterwards it is a data
/// migration rather than a schema edit.
/// </remarks>
public class ApiQuotaWindow : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Matches <see cref="Entities.PriceSource.Code"/>, or a news/macro provider code later.</summary>
    public string SourceCode { get; set; } = null!;

    /// <summary>
    /// Opaque identifier for the accounting period, produced by the governor from the
    /// provider's documented reset semantics (e.g. <c>2026-07</c> for calendar-month-UTC).
    /// Opaque on purpose: providers reset on different boundaries and this column should not
    /// have to change when a new one is added.
    /// </summary>
    public string PeriodKey { get; set; } = null!;

    public DateTimeOffset PeriodStartsAt { get; set; }

    public DateTimeOffset PeriodEndsAt { get; set; }

    /// <summary>Requests allowed in this period. Copied from config at row creation so that
    /// changing the configured limit does not retroactively rewrite history.</summary>
    public int RequestLimit { get; set; }

    /// <summary>Requests consumed. Only ever incremented by the governor's atomic acquire.</summary>
    public int RequestsUsed { get; set; }

    /// <summary>
    /// Set when the provider itself rejected us for quota (429/402). Authoritative: our local
    /// count can only under-count (a request made but not recorded), so a provider rejection
    /// is a correction downward in remaining budget.
    /// </summary>
    public DateTimeOffset? ProviderRejectedAt { get; set; }
}
