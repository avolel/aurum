using Aurum.Api.Shared.Entities;

namespace Aurum.Api.Modules.Macro.Entities;

/// <summary>
/// A macroeconomic time series (CPI, Fed Funds, 10Y, DXY, VIX …). Ingestion lands in
/// Phase 2a; the schema exists now so the first migration is the only one that touches
/// the base tables.
/// </summary>
public class MacroSeries : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Provider's series identifier, e.g. FRED's <c>CPIAUCSL</c>.</summary>
    public string Code { get; set; } = null!;

    /// <summary>Which upstream owns this series: <c>fred</c>, <c>bls</c>, <c>treasury</c>.</summary>
    public string Provider { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Units { get; set; }

    /// <summary>Provider-reported cadence, e.g. <c>Monthly</c>. Free text — providers disagree on vocabulary.</summary>
    public string? Frequency { get; set; }

    public ICollection<MacroObservation> Observations { get; set; } = new List<MacroObservation>();
}
