namespace Aurum.App.Application.Prices.DTOs;

/// <param name="Direction">A <c>PriceEventDirection</c> value.</param>
/// <param name="TriggeredRule">The rule as it stood when the event was saved, so it explains itself after thresholds change.</param>
public sealed record PriceEventDto(
    string Symbol,
    DateTimeOffset DetectedAt,
    string WindowCode,
    DateTimeOffset WindowStartedAt,
    DateTimeOffset WindowEndedAt,
    string Direction,
    decimal StartMid,
    decimal EndMid,
    decimal DeltaAbsolute,
    decimal DeltaPercent,
    decimal VelocityPercentPerMinute,
    decimal? Volatility,
    int SampleCount,
    string SourceCode,
    string BaselineSourceCode,
    bool IsCrossSource,
    string ThresholdProfile,
    string TriggeredRule);
