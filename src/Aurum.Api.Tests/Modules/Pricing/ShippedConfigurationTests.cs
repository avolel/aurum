using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The configuration in the repository must satisfy its own validator.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here builds configuration in memory, which tests the rules and not the file.
/// That gap shipped a real defect: <c>PricePolling:PollInterval</c> was lowered to five minutes
/// when the two backup sources landed, but <c>goldapi.io</c> stayed at <c>Priority 1</c> with a
/// 100-request month. Only the primary has to fund the cadence (D-10), so the ~11,000 requests
/// sitting at Priority 2 and 3 counted for nothing and the host refused to start. Every unit test
/// passed, because none of them read <c>appsettings.json</c>.
/// </para>
/// <para>
/// This deliberately excludes credentials. <c>appsettings.json</c> ships every <c>ApiKey</c> as the
/// empty string on purpose (D-9), so asserting the file validates *as shipped* would mean asserting
/// it carries a placeholder key — putting back the hole that let the process boot without
/// credentials and spend the month one 401 at a time. Keys are supplied here the way an operator
/// supplies them, through an override layer standing in for <c>.env</c>.
/// </para>
/// </remarks>
public class ShippedConfigurationTests
{
    /// <summary>Linked from the API project by the test csproj; see the comment there.</summary>
    private const string ShippedSettingsFile = "appsettings.shipped.json";

    /// <summary>
    /// The real file, plus credentials, must pass the validator that gates host startup.
    /// </summary>
    [Fact]
    public void Shipped_appsettings_passes_validation()
    {
        using var host = PricingOptionsHost.Build(
            BuildConfiguration(),
            GoldApiIoSource.SourceCode, ApiNinjasSource.SourceCode, MetalPriceApiSource.SourceCode);

        // Throws the boot failure, with its message, if the shipped file does not validate.
        _ = host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;
    }

    /// <summary>
    /// The cadence guard is the rule the shipped file actually broke, so assert the primary's
    /// budget covers the shipped interval rather than trusting the aggregate. This fails if someone
    /// lowers <c>PollInterval</c> or reorders <c>Priority</c> without checking the two together.
    /// </summary>
    [Fact]
    public void Shipped_primary_budget_funds_the_shipped_cadence()
    {
        var config = BuildConfiguration();
        var options = BindSources(config);
        var polling = config.GetSection(PricePollingOptions.SectionName).Get<PricePollingOptions>()!;
        using var host = PricingOptionsHost.Build(config);
        var resilience = host.GetRequiredService<IOptions<PriceFeedResilienceOptions>>().Value;

        var primary = options.Sources.Values
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .First();

        // 31 days is the validator's worst case: over-estimating the days under-estimates the
        // spend, which is the direction that costs a month of budget rather than a few polls.
        var pollsPerPeriod = PriceSourcesOptionsValidator.LongestPeriod / polling.PollInterval;

        // A poll is not a request. Every attempt re-enters QuotaHandler and is charged its own
        // lease, and the breaker does not bound that: SourceCircuit observes poll outcomes, so a
        // provider that fails twice then succeeds sustains the full multiple while looking healthy.
        var requestsPerPeriod = pollsPerPeriod * resilience.MaxAttempts;

        Assert.True(
            requestsPerPeriod <= primary.MonthlyRequestLimit,
            $"Primary '{primary.SourceCode}' allows {primary.MonthlyRequestLimit} requests per "
          + $"period but PollInterval {polling.PollInterval} implies {pollsPerPeriod:F0} polls, "
          + $"which MaxAttempts {resilience.MaxAttempts} charges as {requestsPerPeriod:F0} requests.");
    }

    /// <summary>
    /// Every enabled source's TotalTimeout must fit the shipped MaxAttempts and its backoff.
    /// </summary>
    /// <remarks>
    /// The rule the shipped file broke this time. All three sources shipped <c>00:00:35</c>, which
    /// is three 10s attempts with the 2s + 4s of backoff between them left out — the last attempt
    /// ran on nine seconds of its configured ten, spent its lease, and nothing said so. Asserted
    /// against the validator's own formula rather than a literal, so the two cannot drift.
    /// </remarks>
    [Fact]
    public void Shipped_total_timeouts_fit_the_shipped_attempt_count()
    {
        var config = BuildConfiguration();
        var options = BindSources(config);
        using var host = PricingOptionsHost.Build(config);
        var resilience = host.GetRequiredService<IOptions<PriceFeedResilienceOptions>>().Value;

        foreach (var source in options.Sources.Values.Where(source => source.Enabled))
        {
            var required = PriceSourcesOptionsValidator.MinimumTotalTimeout(
                source.RequestTimeout, resilience.MaxAttempts, resilience.RetryBackoffBase);

            Assert.True(
                source.TotalTimeout >= required,
                $"'{source.SourceCode}' ships TotalTimeout {source.TotalTimeout} but "
              + $"MaxAttempts {resilience.MaxAttempts} at RequestTimeout {source.RequestTimeout} "
              + $"needs {required}.");
        }
    }

    /// <summary>
    /// Every source the module registers in code must have an entry in the shipped file. The
    /// validator enforces this at boot from <c>RegisteredPriceSource</c> tags; here the codes are
    /// named explicitly so adding a source without configuring it fails in CI rather than at
    /// startup.
    /// </summary>
    [Fact]
    public void Shipped_appsettings_configures_every_registered_source()
    {
        var options = BindSources(BuildConfiguration());

        Assert.NotNull(options.RequireByCode(GoldApiIoSource.SourceCode));
        Assert.NotNull(options.RequireByCode(ApiNinjasSource.SourceCode));
        Assert.NotNull(options.RequireByCode(MetalPriceApiSource.SourceCode));
    }

    /// <summary>
    /// The shipped buffer must hold one day plus slack at the shipped cadence, or the 1d window
    /// never has a starting price. Same gap as the cadence guard above: the unit tests check the
    /// rule, this checks the file.
    /// </summary>
    [Fact]
    public void Shipped_delta_buffer_covers_the_one_day_window()
    {
        var config = BuildConfiguration();
        var polling = config.GetSection(PricePollingOptions.SectionName).Get<PricePollingOptions>()!;
        var deltas = config.GetSection(DeltaEngineOptions.SectionName).Get<DeltaEngineOptions>()
            ?? new DeltaEngineOptions();

        Assert.True(
            DeltaEngineOptions.BufferCoversLongestWindow(deltas, polling.PollInterval),
            $"MaxSamplesPerSymbol {deltas.MaxSamplesPerSymbol} is below the "
          + $"{deltas.RequiredSamples(polling.PollInterval)} needed at PollInterval {polling.PollInterval}.");
    }

    /// <summary>
    /// The shipped file, then the credentials an operator supplies through <c>.env</c>. Layer order
    /// matters: the in-memory collection has to come second to override the empty keys.
    /// </summary>
    private static IConfigurationRoot BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsFile, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PriceSources:GoldApiIo:ApiKey"] = "test-key",
                ["PriceSources:ApiNinjas:ApiKey"] = "test-key",
                ["PriceSources:MetalpriceApi:ApiKey"] = "test-key",
            })
            .Build();

    /// <summary>Mirrors PricingModule's binding onto the dictionary rather than the options object.</summary>
    private static PriceSourcesOptions BindSources(IConfiguration config)
    {
        var options = new PriceSourcesOptions();
        config.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources);
        return options;
    }
}
