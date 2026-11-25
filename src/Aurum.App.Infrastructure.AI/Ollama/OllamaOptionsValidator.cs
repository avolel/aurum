using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>
/// Checks what <c>ValidateDataAnnotations()</c> can't reach: the job map's entries, and the
/// time limits the resilience handler is built from.
/// </summary>
internal sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        var failures = new List<string>();
        var path = OllamaOptions.SectionName;

        // A missing job would only fail later, when item 6 first asks for it.
        foreach (var job in Enum.GetValues<OllamaJob>())
        {
            var jobPath = $"{path}:Jobs:{job}";

            if (!options.Jobs.TryGetValue(job, out var jobOptions))
            {
                failures.Add($"{jobPath} is missing. Every job needs a Model and a ContextWindow.");
                continue;
            }

            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(jobOptions, new ValidationContext(jobOptions), results, validateAllProperties: true))
            {
                failures.AddRange(results.Select(r => $"{jobPath}: {r.ErrorMessage}"));
            }

            // Ollama treats 0 as "use the model's default", which hides how much fits.
            if (jobOptions.ContextWindow <= 0)
            {
                failures.Add($"{jobPath}:ContextWindow must be positive.");
            }
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{path}:RequestTimeout must be a positive interval.");
        }

        // Polly refuses zero retries, and the standard handler needs its total timeout to be longer
        // than one attempt's. One attempt can't satisfy either.
        if (options.MaxAttempts < 2)
        {
            failures.Add($"{path}:MaxAttempts must be at least 2.");
        }

        if (options.Temperature < 0)
        {
            failures.Add($"{path}:Temperature must not be negative.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
