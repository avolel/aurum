using System.ComponentModel.DataAnnotations;

namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// Bound from the <c>Significance</c> configuration section: how big a move must be before it is
/// saved as an event (BR-02).
/// </summary>
/// <remarks>
/// No defaults in code. The binder can add dictionary keys but not remove them, so a window
/// defaulted here could never be switched off from configuration. A missing section fails boot on
/// <see cref="ThresholdProfile"/> rather than running with no thresholds.
/// </remarks>
public class SignificanceOptions
{
    public const string SectionName = "Significance";

    /// <summary>Saved on every event, so a row says which set of thresholds produced it.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ThresholdProfile { get; set; } = string.Empty;

    /// <summary>
    /// How much bigger a move must be when its two prices came from different sources, because
    /// part of it may be the gap between two providers.
    /// </summary>
    public decimal CrossSourceMagnitudeMultiplier { get; set; }

    /// <summary>
    /// Thresholds keyed by <see cref="DeltaWindow.Code"/>. A window missing from the map is never
    /// classified. Get-only so the binder keeps the case-insensitive comparer.
    /// </summary>
    public Dictionary<string, SignificanceWindowOptions> Windows { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every key is a real window code. A typo would otherwise switch that window off silently.
    /// </summary>
    internal static bool WindowKeysAreKnown(SignificanceOptions options) =>
        options.Windows.Keys.All(key =>
            DeltaWindow.All.Any(window => string.Equals(window.Code, key, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Every threshold and waiting period is positive, and the multiplier never lowers the bar.
    /// </summary>
    internal static bool ValuesAreInRange(SignificanceOptions options) =>
        options.CrossSourceMagnitudeMultiplier >= 1
        && options.Windows.Values.All(window => window.MinPercent > 0 && window.Cooldown > TimeSpan.Zero);
}

public class SignificanceWindowOptions
{
    /// <summary>Smallest move worth an event, in percent: 0.25 means 0.25%.</summary>
    public decimal MinPercent { get; set; }

    /// <summary>After an event, how long the same symbol and window stay quiet.</summary>
    public TimeSpan Cooldown { get; set; }
}
