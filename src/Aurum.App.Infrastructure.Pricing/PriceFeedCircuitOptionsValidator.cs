using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing;

internal class PriceFeedCircuitOptionsValidator(IOptions<PricePollingOptions> polling) :
    IValidateOptions<PriceFeedCircuitOptions>
{
    public ValidateOptionsResult Validate(string? name, PriceFeedCircuitOptions options)
    {
        var failures = new List<string>();
        var path = PriceFeedCircuitOptions.SectionName;

        if (options.FailureThreshold < 1)
            failures.Add($"{path}:{nameof(options.FailureThreshold)} must be at least 1");
        if (options.BreakDuration <= TimeSpan.Zero)
            failures.Add($"{path}:{nameof(options.BreakDuration)} must be a positive time span");

        // PricingModule's PollInterval lambda already reports a non-positive cadence
        // against the key an operator edits — same guard as ValidateTimeouts uses.
        var pollInterval = polling.Value.PollInterval;
        if (pollInterval > TimeSpan.Zero && options.BreakDuration <= pollInterval)
            failures.Add($"{path}:{nameof(options.BreakDuration)} must be greater than {nameof(pollInterval)} ({pollInterval})");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
