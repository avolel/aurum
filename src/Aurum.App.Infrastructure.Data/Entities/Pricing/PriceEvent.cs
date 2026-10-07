using Aurum.App.SharedKernel.Constants;
using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Pricing;

/// <summary>
/// A price move over one window that crossed its threshold (BR-02). An ordinary table, not a
/// hypertable, so it never inherits <c>price_ticks</c>' 30-day retention (D-18).
/// </summary>
public class PriceEvent : AuditableEntity
{
    public long Id { get; set; }

    public string Symbol { get; set; } = SupportedSymbol.Gold;

    /// <summary>The snapshot's <c>AsOf</c>: when the app measured the move.</summary>
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>A <c>DeltaWindow.Code</c>, e.g. <c>5m</c>.</summary>
    public string WindowCode { get; set; } = null!;

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Price time of the end sample. Part of the unique key, and what the cooldown is measured on.</summary>
    public DateTimeOffset WindowEndedAt { get; set; }

    /// <summary>A <see cref="PriceEventDirection"/> value.</summary>
    public string Direction { get; set; } = null!;

    public decimal StartMid { get; set; }

    public decimal EndMid { get; set; }

    public decimal DeltaAbsolute { get; set; }

    public decimal DeltaPercent { get; set; }

    public decimal VelocityPercentPerMinute { get; set; }

    public decimal? Volatility { get; set; }

    public int SampleCount { get; set; }

    /// <summary>Source of the end price. FK to <see cref="PriceSource.Code"/>.</summary>
    public string SourceCode { get; set; } = null!;

    /// <summary>Source of the start price. FK to <see cref="PriceSource.Code"/>.</summary>
    public string BaselineSourceCode { get; set; } = null!;

    public bool IsCrossSource { get; set; }

    /// <summary>The thresholds' configuration name, since what an event means depends on them.</summary>
    public string ThresholdProfile { get; set; } = null!;

    /// <summary>The effective rule as text, so the row explains itself if thresholds change.</summary>
    public string TriggeredRule { get; set; } = null!;
}
