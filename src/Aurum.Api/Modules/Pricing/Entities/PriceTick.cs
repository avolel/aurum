using Aurum.Api.Shared.Entities;

namespace Aurum.Api.Modules.Pricing.Entities;

/// <summary>
/// The canonical normalized quote (FR-1.2). Backed by a TimescaleDB hypertable
/// partitioned on <see cref="ObservedAt"/>.
/// </summary>
/// <remarks>
/// The primary key is composite — (<see cref="ObservedAt"/>, <see cref="Id"/>) — because
/// TimescaleDB rejects any unique index that does not include the partitioning column.
/// A surrogate-only PK will make create_hypertable fail.
/// </remarks>
public class PriceTick : AuditableEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Instrument, e.g. <c>XAUUSD</c>. Present from the first migration (D-4) so Phase 6's
    /// multi-metal work is a code change rather than a migration against a populated hypertable.
    /// </summary>
    public string Symbol { get; set; } = PriceSymbols.Gold;

    /// <summary>When the upstream provider says the quote was valid.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>
    /// When we received it. <c>ReceivedAt - ObservedAt</c> is the staleness we owe the user
    /// in the UI, not just the DB (§14: "delayed data misleading users").
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public decimal? Bid { get; set; }

    public decimal? Ask { get; set; }

    /// <summary>Mid price. Provider-supplied where available, else (bid+ask)/2.</summary>
    public decimal Mid { get; set; }

    /// <summary>FK to <see cref="PriceSource.Code"/> — stable, human-readable, and safe in logs.</summary>
    public string SourceCode { get; set; } = null!;

    public PriceSource? Source { get; set; }
}

public static class PriceSymbols
{
    public const string Gold = "XAUUSD";
}
