using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Macro;

/// <summary>
/// A macroeconomic time series (CPI, Fed Funds, 10Y, the Fed's broad dollar index, VIX …). Loaded from Phase 2a.
/// </summary>
public class MacroSeries : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Provider's series identifier, e.g. FRED's <c>CPIAUCSL</c>.</summary>
    public string Code { get; set; } = null!;

    /// <summary>Which upstream serves this series. Only <c>fred</c> today; BLS and Treasury figures come through FRED (D-22).</summary>
    public string Provider { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Units { get; set; }

    /// <summary>Provider-reported cadence, e.g. <c>Monthly</c>. Free text — providers disagree on vocabulary.</summary>
    public string? Frequency { get; set; }

    public ICollection<MacroObservation> Observations { get; set; } = new List<MacroObservation>();
}
