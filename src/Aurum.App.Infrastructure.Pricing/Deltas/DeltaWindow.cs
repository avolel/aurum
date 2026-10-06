namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// One of the fixed time windows a price move is measured over (FR-1.4).
/// </summary>
/// <remarks>
/// Not configurable: the classifier's (item 6) thresholds are chosen for exactly these windows.
/// </remarks>
public sealed record DeltaWindow(string Code, TimeSpan Length)
{
    public static readonly DeltaWindow OneMinute = new("1m", TimeSpan.FromMinutes(1));
    public static readonly DeltaWindow FiveMinutes = new("5m", TimeSpan.FromMinutes(5));
    public static readonly DeltaWindow FifteenMinutes = new("15m", TimeSpan.FromMinutes(15));
    public static readonly DeltaWindow OneHour = new("1h", TimeSpan.FromHours(1));
    public static readonly DeltaWindow FourHours = new("4h", TimeSpan.FromHours(4));
    public static readonly DeltaWindow OneDay = new("1d", TimeSpan.FromDays(1));

    /// <summary>Every window, shortest first. Declared after the instances it lists.</summary>
    public static readonly IReadOnlyList<DeltaWindow> All =
        [OneMinute, FiveMinutes, FifteenMinutes, OneHour, FourHours, OneDay];

    /// <summary>The window the buffer and the warm-up are sized for.</summary>
    public static DeltaWindow Longest => OneDay;
}
