using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aurum.App.Infrastructure.Pricing;

/// <summary>Bound from the <c>PriceSources</c> configuration section.</summary>
/// <remarks>
/// <para>
/// The section binds to a map rather than to one property per provider. A property per provider
/// means every consumer that needs "the options for source X" has to translate a runtime source
/// code into a compile-time member, and the only way to write that is a switch with a fall-through
/// arm. That arm is a fabricated configuration: it looks identical to a real one downstream, so a
/// source with a 20-request tier gets accounted against whatever the fall-through guessed. There is
/// no correct default for another provider's budget, so the lookup here has no default at all —
/// <see cref="TryGetByCode"/> misses, and callers fail rather than invent.
/// </para>
/// <para>
/// The map is keyed by a friendly config key (<c>GoldApiIo</c>), not by the source code, with the
/// code carried inside as <see cref="PriceSourceOptions.SourceCode"/>. Keying by the code directly
/// would read better but puts a dot in every environment variable name
/// (<c>PriceSources__goldapi.io__ApiKey</c>), which the dotenv parsers in the compose toolchain do
/// not handle consistently. <see cref="PriceSourcesOptionsValidator"/> enforces that every entry
/// declares a code and that no two entries share one, which is what the friendly key costs.
/// </para>
/// </remarks>
public class PriceSourcesOptions
{
    public const string SectionName = "PriceSources";

    /// <summary>
    /// Configured sources, keyed by config key. Get-only and pre-populated because the binder
    /// binds *into* this instance, which is what preserves the case-insensitive comparer —
    /// configuration keys are case-insensitive, so a case-sensitive map would miss lookups that
    /// the config system considers the same key.
    /// </summary>
    public Dictionary<string, PriceSourceOptions> Sources { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enabled sources in failover order: the head is the primary, the rest are backups in the
    /// order the chain will try them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method with two callers, on purpose. <c>FailoverPriceFeed</c> decides which source
    /// serves a poll and <c>PricePollingService</c>'s startup coverage log states which budget
    /// funds the cadence; while those were two <c>OrderBy</c> chains over the same data in two
    /// files they agreed only by coincidence, and the failure mode is a log that confidently
    /// names a primary that is not the one being called.
    /// </para>
    /// <para>
    /// The tie-break on <see cref="PriceSourceOptions.SourceCode"/> is not cosmetic.
    /// <see cref="PriceSourcesOptionsValidator"/> refuses a tie for the <em>lowest</em> priority,
    /// but ties further down are allowed, and without a deterministic second key which backup
    /// runs first would fall out of DI registration order — the exact implicitness D-12 rejected
    /// keyed DI to avoid. Ordinal because source codes are identifiers, not user text.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PriceSourceOptions> EnabledInFailoverOrder() =>
        [.. Sources.Values
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .ThenBy(source => source.SourceCode, StringComparer.Ordinal)];

    /// <summary>
    /// Resolves a source's configuration by its natural key — the same string
    /// <c>QuotaHandler</c>, <c>IPriceSource.Code</c> and <c>api_quota_windows.SourceCode</c>
    /// all use. A linear scan is deliberate: the map holds a handful of entries, and caching an
    /// index in a mutable options object would go stale under <c>IOptionsMonitor</c> reloads.
    /// </summary>
    public bool TryGetByCode(string sourceCode, [NotNullWhen(true)] out PriceSourceOptions? options)
    {
        foreach (var candidate in Sources.Values)
        {
            if (string.Equals(candidate.SourceCode, sourceCode, StringComparison.OrdinalIgnoreCase))
            {
                options = candidate;
                return true;
            }
        }

        options = null;
        return false;
    }

    /// <summary>
    /// <see cref="TryGetByCode"/> for the callers that cannot proceed without the configuration.
    /// </summary>
    /// <remarks>
    /// Throwing is the point. There is no defensible default for another provider's budget or
    /// period: guessing high lets a source spend budget it does not have, and guessing the period
    /// kind rolls our counter on a different day from the provider's, so the ledger and the
    /// account disagree with nothing in either to say so. Under normal wiring this is unreachable
    /// — <see cref="PriceSourcesOptionsValidator"/> has already run at boot — so it is a backstop
    /// for a source registered in code but absent from configuration.
    /// </remarks>
    public PriceSourceOptions RequireByCode(string sourceCode)
    {
        if (TryGetByCode(sourceCode, out var options))
        {
            return options;
        }

        throw new InvalidOperationException(
            $"No configuration for price source '{sourceCode}'. Add an entry under "
          + $"{SectionName} whose SourceCode is '{sourceCode}'.");
    }
}

/// <summary>
/// One upstream price provider's configuration.
/// </summary>
/// <remarks>
/// The annotations here are enforced by <see cref="PriceSourcesOptionsValidator"/>, not by
/// <c>ValidateDataAnnotations()</c>. That call validates the attributes on
/// <see cref="PriceSourcesOptions"/>' own properties and does not descend into the objects behind
/// them, so every attribute on this class went unenforced for as long as it was reached through a
/// nested property.
/// </remarks>
public class PriceSourceOptions
{
    /// <summary>
    /// The provider's natural key, e.g. <c>goldapi.io</c>. This — not the config key above it —
    /// is what the quota ledger, the source registry and the poller all match on.
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
    /// Required by <see cref="QuotaPeriodKind.RollingThirtyDays"/> and ignored otherwise: the
    /// date the provider's rolling window counts from, typically signup.
    /// <see cref="PriceSourcesOptionsValidator"/> rejects the combination at boot, because the
    /// period resolver throws on every acquire without one.
    /// </summary>
    public DateTimeOffset? PeriodAnchor { get; set; }

    /// <summary>Budget for a single attempt, enforced inside the retry loop.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ceiling on the whole retry sequence — every attempt plus every backoff delay. Must exceed
    /// <see cref="RequestTimeout"/>, or the first attempt consumes the total budget and the retries
    /// are cancelled before they open a socket; <see cref="PriceSourcesOptionsValidator"/> enforces
    /// that, since the symptom is a retry policy that silently does nothing.
    /// </summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(35);
}

/// <summary>Bound from the <c>PricePolling</c> configuration section.</summary>
/// <remarks>
/// The poller runs one timer and asks the failover chain for one price per tick, so the cadence
/// belongs to the feed rather than to any source. It cannot live under <c>PriceSources</c>: that
/// section binds its children into the source map, so a scalar there becomes a source named
/// "PollInterval" with no SourceCode.
/// </remarks>
public class PricePollingOptions
{
    public const string SectionName = "PricePolling";

    public TimeSpan PollInterval { get; set; }
}

public enum QuotaPeriodKind
{
    /// <summary>Resets at 00:00 UTC on the first of each month.</summary>
    CalendarMonthUtc,

    /// <summary>Resets every 30 days from a per-source anchor date (e.g. signup).</summary>
    RollingThirtyDays,
}
