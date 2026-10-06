using System.ComponentModel.DataAnnotations;

namespace Aurum.App.Infrastructure.Pricing;

/// <summary>
/// Bound from the <c>PriceSources</c> configuration section: a map keyed by a friendly config key,
/// with the provider's code inside each entry (D-9).
/// </summary>
public class PriceSourcesOptions
{
    public const string SectionName = "PriceSources";

    /// <summary>
    /// Configured sources, keyed by config key. Get-only and pre-populated so the binder binds into
    /// it and keeps the case-insensitive comparer, matching how configuration keys compare.
    /// </summary>
    public Dictionary<string, PriceSourceOptions> Sources { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enabled sources in failover order: the head is the primary, the rest are backups.
    /// </summary>
    /// <remarks>
    /// The one ordering both <c>FailoverPriceFeed</c> and the poller's coverage log use, so they
    /// cannot name different primaries. The <c>SourceCode</c> tie-break keeps backup order from
    /// depending on registration order (D-12).
    /// </remarks>
    public IReadOnlyList<PriceSourceOptions> EnabledInFailoverOrder() =>
        [.. Sources.Values
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .ThenBy(source => source.SourceCode, StringComparer.Ordinal)];

    /// <summary>
    /// Resolves a source's configuration by its code. Throws on a miss: there is no safe default for
    /// another provider's budget (D-9). A linear scan, because a cached index would go stale on reload.
    /// </summary>
    public PriceSourceOptions RequireByCode(string sourceCode) =>
        Sources.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceCode, sourceCode, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"No configuration for price source '{sourceCode}'. Add an entry under "
          + $"{SectionName} whose SourceCode is '{sourceCode}'.");
}

/// <summary>
/// One upstream price provider's configuration. Its annotations are enforced by
/// <see cref="PriceSourcesOptionsValidator"/>, not by <c>ValidateDataAnnotations()</c> (D-9).
/// </summary>
public class PriceSourceOptions
{
    /// <summary>
    /// The provider's code, e.g. <c>goldapi.io</c>. What the quota ledger and the source registry
    /// match on, not the config key.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "SourceCode is required.")]
    public string SourceCode { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "ApiKey is required.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "BaseUrl is required.")]
    public Uri? BaseUrl { get; set; }

    public int Priority { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Requests allowed per period. Low by default so a misconfiguration under-polls.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MonthlyRequestLimit { get; set; } = 100;

    /// <summary>
    /// How the provider's quota period rolls. Check the account page, not the docs: a wrong choice
    /// fails silently.
    /// </summary>
    public QuotaPeriodKind QuotaPeriod { get; set; } = QuotaPeriodKind.CalendarMonthUtc;

    /// <summary>
    /// The date a <see cref="QuotaPeriodKind.RollingThirtyDays"/> window counts from, usually signup.
    /// Required for that kind and ignored otherwise (D-7).
    /// </summary>
    public DateTimeOffset? PeriodAnchor { get; set; }

    /// <summary>Budget for a single attempt, enforced inside the retry loop.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ceiling on the whole retry sequence, backoff included. 40s because three 10s attempts at a 2s
    /// base need 36s (D-15); <c>PriceSourcesOptionsValidator.MinimumTotalTimeout</c> checks it.
    /// </summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(40);
}

/// <summary>Bound from the <c>PricePolling</c> configuration section.</summary>
/// <remarks>
/// Not under <c>PriceSources</c>: that section binds its children into the source map, so a scalar
/// there would become a source named "PollInterval".
/// </remarks>
public class PricePollingOptions
{
    public const string SectionName = "PricePolling";

    public TimeSpan PollInterval { get; set; }

    /// <summary>
    /// Age beyond which the latest-quote cache flags a price as stale. Null means twice
    /// <see cref="PollInterval"/>, which is why it lives in this section (D-16).
    /// </summary>
    public TimeSpan? StaleAfter { get; set; }

    /// <summary>
    /// <see cref="StaleAfter"/> is unset, or longer than one <see cref="PollInterval"/>. A method so the
    /// test calls the rule the host runs.
    /// </summary>
    internal static bool StaleAfterExceedsPollInterval(PricePollingOptions options) =>
        options.StaleAfter is not { } staleAfter || staleAfter > options.PollInterval;
}

public enum QuotaPeriodKind
{
    /// <summary>Resets at 00:00 UTC on the first of each month.</summary>
    CalendarMonthUtc,

    /// <summary>Resets every 30 days from a per-source anchor date (e.g. signup).</summary>
    RollingThirtyDays,
}

public class PriceFeedResilienceOptions
{
    public const string SectionName = "PriceFeed:Resilience";

    /// <summary>
    /// Total HTTP requests one poll may spend, including the first. Attempts, not retries, because
    /// each attempt is billed (D-15).
    /// </summary>
    [Range(1, 5)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// First retry delay; each later retry doubles it. Explicit so the timeout validator models a
    /// value it can see (D-15).
    /// </summary>
    public TimeSpan RetryBackoffBase { get; set; } = TimeSpan.FromSeconds(2);
}
