using Aurum.Api.Modules.Pricing;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// Builds the options a governor needs for a test's synthetic source code.
/// </summary>
/// <remarks>
/// The governor used to fall back to a hardcoded 100-request calendar-month budget for any source
/// it had no options for, so these tests ran on an invented configuration without saying so. Now an
/// unconfigured source throws, and a test that cares about a limit has to state it — which is the
/// point: the limit a test asserts against is the limit it configured.
/// </remarks>
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
}
