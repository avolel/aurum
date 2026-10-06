using Aurum.App.SharedKernel.Constants;
using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Pricing;

/// <summary>
/// The canonical normalized quote (FR-1.2). Backed by a TimescaleDB hypertable
/// partitioned on <see cref="ObservedAt"/>.
/// </summary>
/// <remarks>
/// The key is (<see cref="ObservedAt"/>, <see cref="Id"/>): TimescaleDB rejects a unique index
/// without the partitioning column.
/// </remarks>
public class PriceTick : AuditableEntity
{
    public long Id { get; set; }

    /// <summary>Instrument, e.g. <c>XAUUSD</c> (D-4).</summary>
    public string Symbol { get; set; } = SupportedSymbol.Gold;

    /// <summary>When the upstream provider says the quote was valid.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>When the app received it.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public decimal? Bid { get; set; }

    public decimal? Ask { get; set; }

    /// <summary>Mid price. Provider-supplied where available, else (bid+ask)/2.</summary>
    public decimal Mid { get; set; }

    /// <summary>FK to <see cref="PriceSource.Code"/> — stable, human-readable, and safe in logs.</summary>
    public string SourceCode { get; set; } = null!;

    public PriceSource? Source { get; set; }
}
