using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Pricing.Quota;

/// <summary>
/// Durable per-source, per-period request counter. One row per
/// (<see cref="SourceCode"/>, <see cref="PeriodKey"/>).
/// </summary>
public class ApiQuotaWindow : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Matches <see cref="Entities.PriceSource.Code"/>, or a news/macro provider code later.</summary>
    public string SourceCode { get; set; } = null!;

    /// <summary>
    /// Opaque period id from the governor, e.g. <c>2026-07</c>. Opaque so a new reset rule needs
    /// no schema change.
    /// </summary>
    public string PeriodKey { get; set; } = null!;

    public DateTimeOffset PeriodStartsAt { get; set; }

    public DateTimeOffset PeriodEndsAt { get; set; }

    /// <summary>Requests allowed. Copied from config when the row is created, so later changes
    /// do not rewrite history.</summary>
    public int RequestLimit { get; set; }

    /// <summary>Requests consumed. Only ever incremented by the governor's atomic acquire.</summary>
    public int RequestsUsed { get; set; }

    /// <summary>
    /// Set when the provider rejected the app for quota (429/402). Remaining budget is then zero.
    /// </summary>
    public DateTimeOffset? ProviderRejectedAt { get; set; }
}
