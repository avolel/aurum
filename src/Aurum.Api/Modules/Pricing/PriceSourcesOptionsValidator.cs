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
public class PriceSourcesOptionsValidator : IValidateOptions<PriceSourcesOptions>
{
    /// <summary>
    /// Worst case for a calendar month. Over-estimating the days would under-estimate the spend,
    /// which is the direction that costs a month of budget rather than a few polls.
    /// </summary>
    private static readonly TimeSpan LongestPeriod = TimeSpan.FromDays(31);

    public ValidateOptionsResult Validate(string? name, PriceSourcesOptions options)
    {
        var failures = new List<string>();
        var seenCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            ValidatePollBudget(path, source, failures);

            // ResolvePeriod throws on every acquire when a rolling period has no anchor, and the
            // acquire path is inside an HTTP handler, so the failure surfaces as a generic poll
            // error on an interval forever. Refuse it at boot instead.
            if (source.QuotaPeriod == QuotaPeriodKind.RollingThirtyDays && source.PeriodAnchor is null)
            {
                failures.Add(
                    $"{path}: QuotaPeriod {nameof(QuotaPeriodKind.RollingThirtyDays)} requires PeriodAnchor.");
            }
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

    private static void ValidatePollBudget(string path, PriceSourceOptions source, List<string> failures)
    {
        if (source.PollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{path}: PollInterval must be positive, was {source.PollInterval}.");
            return;
        }

        if (source.MonthlyRequestLimit < 1)
        {
            // Already reported by the [Range] attribute; returning here only avoids dividing by it.
            return;
        }

        var pollsPerPeriod = LongestPeriod.TotalSeconds / source.PollInterval.TotalSeconds;
        if (pollsPerPeriod <= source.MonthlyRequestLimit)
        {
            return;
        }

        var minimum = TimeSpan.FromSeconds(LongestPeriod.TotalSeconds / source.MonthlyRequestLimit);
        failures.Add(
            $"{path}: PollInterval {source.PollInterval} implies ~{pollsPerPeriod:F0} requests per period " +
            $"but MonthlyRequestLimit is {source.MonthlyRequestLimit}. " +
            // The days component is load-bearing: `hh` is the hour *within* a day, so a minimum
            // spanning days renders as its remainder and hands the operator a value that fails
            // this same guard. Any limit below ~31 crosses that boundary.
            $"Use an interval of at least {minimum:dd\\.hh\\:mm\\:ss}.");
    }
}
