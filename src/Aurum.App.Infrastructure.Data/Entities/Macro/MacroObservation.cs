using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Macro;

public class MacroObservation : AuditableEntity
{
    public long Id { get; set; }

    public int MacroSeriesId { get; set; }

    public MacroSeries Series { get; set; } = null!;

    /// <summary>The period the value describes (e.g. 2026-06-01 for June CPI).</summary>
    public DateOnly ObservedOn { get; set; }

    /// <summary>
    /// When the figure was published. Distinct from <see cref="ObservedOn"/> and the one that
    /// matters for causation: a June CPI print moves the price on its release date, not in June.
    /// </summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Null where the provider reports the period as missing rather than omitting it.</summary>
    public decimal? Value { get; set; }
}
