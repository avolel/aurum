using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Modules.Pricing;

/// <summary>
/// Validates each configured price source at host startup.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> <c>ValidateDataAnnotations()</c> runs
/// <c>Validator.TryValidateObject(..., validateAllProperties: true)</c> on the options object.
/// "All properties" means the attributes declared on that object's own properties — it does not
/// descend into the objects those properties hold. While the sources hung off a nested property,
/// every <c>[Required]</c> and <c>[Range]</c> on them was dead: a missing API key bound to the
/// empty string, boot succeeded, and the process then spent its monthly budget one 401 at a time
/// (401 is not a quota rejection, so nothing clamps, and a spent lease is never refunded).
/// Something has to do the descent explicitly. This is that something.</para>
///
/// <para><b>Why cross-field checks live here rather than in the poller.</b> The poll-budget guard
/// used to run in <c>PricePollingService.ExecuteAsync</c>, which is after the host reports
/// healthy, and it only ever looked at the one hardcoded source. Boot is the right place to
/// refuse a cadence that cannot fit its budget, and a loop over the map is the only version that
/// keeps covering sources added later.</para>
/// </remarks>
internal class PriceSourcesOptionsValidator(
    IEnumerable<RegisteredPriceSource> registered,
    IOptions<PricePollingOptions> polling) : IValidateOptions<PriceSourcesOptions>
{
    /// <summary>
    /// Worst case for a calendar month. Over-estimating the days would under-estimate the spend,
    /// which is the direction that costs a month of budget rather than a few polls.
    /// </summary>
    /// <remarks>Internal so the poller's coverage log measures against the same worst case.</remarks>
    internal static readonly TimeSpan LongestPeriod = TimeSpan.FromDays(31);

    public ValidateOptionsResult Validate(string? name, PriceSourcesOptions options)
    {
        var failures = new List<string>();
        var seenCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Registered codes come from the tags; configured codes come from appsettings / env vars.
        var configuredCodes = options.Sources.Values
            .Select(s => s.SourceCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var registered in registered)
        {
            if (!configuredCodes.Contains(registered.SourceCode))
            {
                failures.Add(
                    $"Price source '{registered.SourceCode}' is registered but has no {PriceSourcesOptions.SectionName} entry.");
            }
        }

        foreach (var (configKey, source) in options.Sources)
        {
            var path = $"{PriceSourcesOptions.SectionName}:{configKey}";

            // SourceCode is checked even for a disabled source: it is the map's real identity, and
            // an entry without one cannot be looked up, enabled later, or reported on.
            if (string.IsNullOrWhiteSpace(source.SourceCode))
            {
                failures.Add($"{path}: SourceCode is required.");
            }
            else if (seenCodes.TryGetValue(source.SourceCode, out var firstKey))
            {
                // Two entries sharing a code would collide on one quota ledger row, so the second
                // would silently spend the first's budget — the failure this whole module exists
                // to prevent, reintroduced through configuration.
                failures.Add(
                    $"{path}: SourceCode '{source.SourceCode}' is already declared by {PriceSourcesOptions.SectionName}:{firstKey}.");
            }
            else
            {
                seenCodes[source.SourceCode] = configKey;
            }

            // A disabled source is never constructed, never polled and never charged, so holding
            // it to credential and cadence requirements would make it impossible to keep a
            // half-configured provider in the file while it is switched off.
            if (!source.Enabled)
            {
                continue;
            }

            ValidateAnnotations(path, source, failures);

            // ResolvePeriod throws on every acquire when a rolling period has no anchor, and the
            // acquire path is inside an HTTP handler, so the failure surfaces as a generic poll
            // error on an interval forever. Refuse it at boot instead.
            if (source.QuotaPeriod == QuotaPeriodKind.RollingThirtyDays && source.PeriodAnchor is null)
            {
                failures.Add(
                    $"{path}: QuotaPeriod {nameof(QuotaPeriodKind.RollingThirtyDays)} requires PeriodAnchor.");
            }
        }

        // Only the primary serves every healthy poll, so only its budget has to cover the whole
        // period. Backups are called during an outage of the sources above them and may run out
        // mid-period; the governor stops them cleanly when they do (D-10).
        if (ResolvePrimarySource(options, failures) is { } primary)
        {
            ValidatePollBudget(
                $"{PriceSourcesOptions.SectionName}:{primary.Key}",
                primary.Value,
                polling.Value.PollInterval,
                failures);
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateAnnotations(string path, PriceSourceOptions source, List<string> failures)
    {
        var results = new List<ValidationResult>();

        // This is the descent ValidateDataAnnotations() does not perform. Here the attributes are
        // on the validated object's own properties, so validateAllProperties reaches them.
        if (Validator.TryValidateObject(source, new ValidationContext(source), results, validateAllProperties: true))
        {
            return;
        }

        // Prefixed with the config path so the message names the key an operator has to edit,
        // rather than a bare property name that could belong to any source in the file.
        failures.AddRange(results.Select(r => $"{path}: {r.ErrorMessage}"));
    }

    /// <summary>
    /// Refuses a cadence the primary source's budget cannot fund for a whole period.
    /// </summary>
    /// <remarks>
    /// The cadence and the budget now live in different sections, so the message names both keys:
    /// either raising the interval or reordering Priority is a valid fix, and only the operator
    /// knows which they meant.
    /// </remarks>
    private static void ValidatePollBudget(
        string path, PriceSourceOptions source, TimeSpan pollInterval, List<string> failures)
    {
        // PricingModule's .Validate lambda rejects a non-positive interval before this runs,
        // and it names the key an operator actually edits (PricePolling:PollInterval).
        if (source.MonthlyRequestLimit < 1)
        {
            return;
        }

        var pollsPerPeriod = LongestPeriod.TotalSeconds / pollInterval.TotalSeconds;
        if (pollsPerPeriod <= source.MonthlyRequestLimit)
        {
            return;
        }

        var minimum = TimeSpan.FromSeconds(LongestPeriod.TotalSeconds / source.MonthlyRequestLimit);
        failures.Add(
            $"{path} is the primary source (Priority {source.Priority}). " +
            $"{PricePollingOptions.SectionName}:PollInterval {pollInterval} implies " +
            $"~{pollsPerPeriod:F0} requests per period but MonthlyRequestLimit is " +
            $"{source.MonthlyRequestLimit}. " +
            // The days component is load-bearing: `hh` is the hour *within* a day, so a minimum
            // spanning days renders as its remainder and hands the operator a value that fails
            // this same guard. Any limit below ~31 crosses that boundary.
            $"Use an interval of at least {minimum:dd\\.hh\\:mm\\:ss}.");
    }

    /// <summary>
    /// The source the feed uses while everything is healthy: the enabled entry with the lowest
    /// <see cref="PriceSourceOptions.Priority"/>. Returns the config key alongside the options
    /// because every failure message has to name the entry an operator would edit.
    /// </summary>
    /// <remarks>
    /// Resolved among enabled entries rather than by <c>Priority == 1</c>, so parking the top
    /// provider hands the budget guarantee to the source that actually serves the polls. A tie for
    /// the lowest priority is refused: the winner would be whichever the DI container yields first
    /// in <c>PricePollingService.PollOnceAsync</c>, so the guarantee would attach to a source
    /// nobody chose. Ties further down the chain are allowed — they only decide which backup
    /// spends its budget first.
    /// </remarks>
    private static KeyValuePair<string, PriceSourceOptions>? ResolvePrimarySource(
        PriceSourcesOptions options, List<string> failures)
    {
        var enabled = options.Sources.Where(entry => entry.Value.Enabled).ToList();

        if (enabled.Count == 0)
        {
            failures.Add(
                $"No {PriceSourcesOptions.SectionName} entry is enabled, so the poller would never "
              + "produce a price. Enable at least one source.");
            return null;
        }

        var lowest = enabled.Min(entry => entry.Value.Priority);
        var atLowest = enabled.Where(entry => entry.Value.Priority == lowest).ToList();

        if (atLowest.Count > 1)
        {
            var keys = atLowest.Select(entry => $"{PriceSourcesOptions.SectionName}:{entry.Key}");
            failures.Add(
                $"{string.Join(" and ", keys)} all declare Priority {lowest}. Priority is the "
              + "failover order, so the source serving every healthy poll — and whose budget the "
              + "cadence check guarantees — would depend on registration order. Note that Priority "
              + "defaults to 1 when the key is absent.");
            return null;
        }

        return atLowest[0];
    }
}
