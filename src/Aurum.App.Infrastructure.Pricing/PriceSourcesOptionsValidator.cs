using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing;

/// <summary>
/// Validates each configured price source at host startup. Runs the per-source annotations that
/// <c>ValidateDataAnnotations()</c> never reaches, plus the cross-field checks (D-9).
/// </summary>
internal class PriceSourcesOptionsValidator(
    IEnumerable<RegisteredPriceSource> registered,
    IOptions<PricePollingOptions> polling,
    IOptions<PriceFeedResilienceOptions> resilience) : IValidateOptions<PriceSourcesOptions>
{
    /// <summary>
    /// The longest calendar month, so the spend is never under-estimated. Internal so the poller's
    /// coverage log uses the same figure.
    /// </summary>
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

            // Checked even when disabled: SourceCode is the entry's identity.
            if (string.IsNullOrWhiteSpace(source.SourceCode))
            {
                failures.Add($"{path}: SourceCode is required.");
            }
            else if (seenCodes.TryGetValue(source.SourceCode, out var firstKey))
            {
                // Two entries with one code would share a quota ledger row (D-9).
                failures.Add(
                    $"{path}: SourceCode '{source.SourceCode}' is already declared by {PriceSourcesOptions.SectionName}:{firstKey}.");
            }
            else
            {
                seenCodes[source.SourceCode] = configKey;
            }

            // A disabled source is never called, so it may sit in the file half-configured.
            if (!source.Enabled)
            {
                continue;
            }

            ValidateAnnotations(path, source, failures);

            // Without an anchor ResolvePeriod throws on every acquire, as a poll error forever.
            if (source.QuotaPeriod == QuotaPeriodKind.RollingThirtyDays && source.PeriodAnchor is null)
            {
                failures.Add(
                    $"{path}: QuotaPeriod {nameof(QuotaPeriodKind.RollingThirtyDays)} requires PeriodAnchor.");
            }

            ValidateTimeouts(
                path,
                source,
                polling.Value.PollInterval,
                resilience.Value.MaxAttempts,
                resilience.Value.RetryBackoffBase,
                failures);
        }

        // Only the primary has to fund the whole period; backups may run out (D-10).
        if (ResolvePrimarySource(options, failures) is { } primary)
        {
            ValidatePollBudget(
                $"{PriceSourcesOptions.SectionName}:{primary.Key}",
                primary.Value,
                polling.Value.PollInterval,
                resilience.Value.MaxAttempts,
                failures);
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateAnnotations(string path, PriceSourceOptions source, List<string> failures)
    {
        var results = new List<ValidationResult>();

        // The descent ValidateDataAnnotations() does not perform.
        if (Validator.TryValidateObject(source, new ValidationContext(source), results, validateAllProperties: true))
        {
            return;
        }

        // Prefixed so the message names the key an operator edits.
        failures.AddRange(results.Select(r => $"{path}: {r.ErrorMessage}"));
    }

    /// <summary>
    /// Refuses timeout values that make the resilience pipeline behave differently from how it
    /// reads. Checked for every source, since a backup's retries run in the same tick (D-13, D-15).
    /// </summary>
    private static void ValidateTimeouts(
        string path,
        PriceSourceOptions source,
        TimeSpan pollInterval,
        int maxAttempts,
        TimeSpan backoffBase,
        List<string> failures)
    {
        if (source.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{path}: RequestTimeout must be a positive interval.");
            return;
        }

        if (source.TotalTimeout <= source.RequestTimeout)
        {
            failures.Add(
                $"{path}: TotalTimeout {source.TotalTimeout} must exceed RequestTimeout "
              + $"{source.RequestTimeout}. TotalTimeout bounds the whole retry sequence, so an "
              + "equal or smaller value cancels every retry before it opens a socket and the retry "
              + "strategy silently does nothing.");
            return;
        }

        var required = MinimumTotalTimeout(source.RequestTimeout, maxAttempts, backoffBase);
        if (source.TotalTimeout < required)
        {
            failures.Add(
                $"{path}: TotalTimeout {source.TotalTimeout} is shorter than the {required} needed "
              + $"for {PriceFeedResilienceOptions.SectionName}:MaxAttempts {maxAttempts} "
              + $"({maxAttempts} × RequestTimeout {source.RequestTimeout} plus exponential backoff "
              + $"from {backoffBase}). The last attempt is cancelled part-way or never starts, so "
              + "MaxAttempts reads as one number and behaves as another — and a truncated attempt "
              + "still spends its lease.");
            return;
        }

        // A non-positive interval is reported by Program.cs's own check.
        if (pollInterval > TimeSpan.Zero && source.TotalTimeout >= pollInterval)
        {
            failures.Add(
                $"{path}: TotalTimeout {source.TotalTimeout} is not shorter than "
              + $"{PricePollingOptions.SectionName}:PollInterval {pollInterval}, so a retrying poll "
              + "can still be in flight when the next tick starts. Overlapping ticks spend quota "
              + "faster than the cadence guard accounts for.");
        }
    }

    /// <summary>
    /// Refuses a cadence the primary source's budget cannot fund for a whole period (D-10, D-15).
    /// </summary>
    private static void ValidatePollBudget(
        string path, PriceSourceOptions source, TimeSpan pollInterval, int maxAttempts, List<string> failures)
    {
        // Reported by the source's own [Range]; returning here avoids a meaningless message.
        if (source.MonthlyRequestLimit < 1)
        {
            return;
        }

        // Reported by MaxAttempts' [Range], which may run after this; a zero would pass everything.
        if (maxAttempts < 1)
        {
            return;
        }

        var pollsPerPeriod = LongestPeriod.TotalSeconds / pollInterval.TotalSeconds;

        // A poll is up to MaxAttempts requests, and the circuit breaker does not bound that (D-15).
        var requestsPerPeriod = pollsPerPeriod * maxAttempts;
        if (requestsPerPeriod <= source.MonthlyRequestLimit)
        {
            return;
        }

        // Includes maxAttempts, or the suggested interval would fail this same check.
        var minimum = TimeSpan.FromSeconds(
            LongestPeriod.TotalSeconds * maxAttempts / source.MonthlyRequestLimit);

        failures.Add(
            $"{path} is the primary source (Priority {source.Priority}). " +
            $"{PricePollingOptions.SectionName}:PollInterval {pollInterval} implies " +
            $"~{pollsPerPeriod:F0} polls per period, and " +
            $"{PriceFeedResilienceOptions.SectionName}:MaxAttempts {maxAttempts} charges up to " +
            $"~{requestsPerPeriod:F0} requests, but MonthlyRequestLimit is " +
            $"{source.MonthlyRequestLimit}. " +
            // Keep `dd`: `hh` is the hour within a day, so a minimum over a day would print short.
            $"Use an interval of at least {minimum:dd\\.hh\\:mm\\:ss}, or lower MaxAttempts.");
    }

    /// <summary>
    /// The enabled entry with the lowest <see cref="PriceSourceOptions.Priority"/>, with its config
    /// key for messages. A tie for lowest is refused; ties further down are allowed (D-10).
    /// </summary>
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

    /// <summary>
    /// Time a full retry sequence needs for every attempt to get its whole RequestTimeout.
    /// </summary>
    /// <remarks>
    /// Rebuilds Polly's exponential schedule (base × 2^i before retry i). Only true while
    /// <c>Program.AddPriceSource</c> sets <c>Delay</c>, <c>BackoffType</c> and <c>UseJitter = false</c>
    /// explicitly (D-15). Internal so the shipped-configuration test runs this rule, not a copy.
    /// </remarks>
    internal static TimeSpan MinimumTotalTimeout(
        TimeSpan requestTimeout, int maxAttempts, TimeSpan backoffBase)
    {
        var backoff = TimeSpan.Zero;
        for (var i = 0; i < maxAttempts - 1; i++)
        {
            backoff += backoffBase * Math.Pow(2, i);
        }

        return requestTimeout * maxAttempts + backoff;
    }
}
