using Aurum.App.Infrastructure.Pricing;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// Builds source options for a test. An unconfigured source throws, so the limit a test asserts
/// against is always one it configured.
/// </summary>
internal static class TestPriceSources
{
    public static IOptions<PriceSourcesOptions> For(
        string sourceCode,
        int monthlyRequestLimit = 100,
        QuotaPeriodKind quotaPeriod = QuotaPeriodKind.CalendarMonthUtc,
        DateTimeOffset? periodAnchor = null)
    {
        var options = new PriceSourcesOptions();
        options.Sources[sourceCode] = new PriceSourceOptions
        {
            SourceCode = sourceCode,
            ApiKey = "test-key",
            BaseUrl = new Uri("https://example.invalid/"),
            MonthlyRequestLimit = monthlyRequestLimit,
            QuotaPeriod = quotaPeriod,
            PeriodAnchor = periodAnchor,
        };

        return Options.Create(options);
    }

    /// <summary>
    /// Builds a multi-source map for the chain tests: priorities, enabled flags and codes, with the
    /// quota fields left at their defaults because the chain never consults them.
    /// </summary>
    public static IOptions<PriceSourcesOptions> ForChain(
        params (string SourceCode, int Priority, bool Enabled)[] entries)
    {
        var options = new PriceSourcesOptions();

        foreach (var (sourceCode, priority, enabled) in entries)
        {
            // Keyed by source code, not a config key: the chain never reads the map key.
            options.Sources[sourceCode] = new PriceSourceOptions
            {
                SourceCode = sourceCode,
                ApiKey = "test-key",
                BaseUrl = new Uri("https://example.invalid/"),
                Priority = priority,
                Enabled = enabled,
            };
        }

        return Options.Create(options);
    }
}
