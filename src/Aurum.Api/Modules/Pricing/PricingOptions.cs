using System.ComponentModel.DataAnnotations;

namespace Aurum.Api.Modules.Pricing;

/// <summary>Bound from the <c>PriceSources</c> configuration section.</summary>
public class PriceSourcesOptions
{
    public const string SectionName = "PriceSources";

    public GoldApiIoOptions GoldApiIo { get; set; } = new();
}

public class GoldApiIoOptions
{
    public const string SourceCode = "goldapi.io";

    [Required(AllowEmptyStrings = false, ErrorMessage = "PriceSources:GoldApiIo:ApiKey is required.")]
    public string ApiKey { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://www.goldapi.io/");

    public int Priority { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Requests allowed per accounting period. GoldAPI's free tier is monthly; the default is
    /// deliberately low so a misconfiguration under-polls rather than burning the month.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MonthlyRequestLimit { get; set; } = 100;

    /// <summary>
    /// How the provider's quota period rolls. Verify against the account page rather than the
    /// docs — a wrong choice here is a silent one-in-twelve failure.
    /// </summary>
    public QuotaPeriodKind QuotaPeriod { get; set; } = QuotaPeriodKind.CalendarMonthUtc;

    /// <summary>
    /// Poll cadence. Must be consistent with <see cref="MonthlyRequestLimit"/>; the poller
    /// refuses to start if the two imply overspending the period.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public enum QuotaPeriodKind
{
    /// <summary>Resets at 00:00 UTC on the first of each month.</summary>
    CalendarMonthUtc,

    /// <summary>Resets every 30 days from a per-source anchor date (e.g. signup).</summary>
    RollingThirtyDays,
}
