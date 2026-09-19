using Aurum.Api.Modules.Pricing;
using Aurum.Api.Modules.Pricing.Sources;
using Microsoft.Extensions.Configuration;
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
        var result = ValidateShipped();

        Assert.False(result.Failed, result.FailureMessage);
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

        var primary = options.Sources.Values
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .First();

        // 31 days is the validator's worst case: over-estimating the days under-estimates the
        // spend, which is the direction that costs a month of budget rather than a few polls.
        var pollsPerPeriod = PriceSourcesOptionsValidator.LongestPeriod / polling.PollInterval;

        Assert.True(
            pollsPerPeriod <= primary.MonthlyRequestLimit,
            $"Primary '{primary.SourceCode}' allows {primary.MonthlyRequestLimit} requests per "
          + $"period but PollInterval {polling.PollInterval} implies {pollsPerPeriod:F0}.");
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

        Assert.True(options.TryGetByCode(GoldApiIoSource.SourceCode, out _));
        Assert.True(options.TryGetByCode(ApiNinjasSource.SourceCode, out _));
        Assert.True(options.TryGetByCode(MetalPriceApiSource.SourceCode, out _));
    }

    private static ValidateOptionsResult ValidateShipped()
    {
        var config = BuildConfiguration();
        var options = BindSources(config);
        var polling = config.GetSection(PricePollingOptions.SectionName).Get<PricePollingOptions>()!;

        var registered = options.Sources.Values
            .Select(source => new RegisteredPriceSource(source.SourceCode));

        return new PriceSourcesOptionsValidator(registered, Options.Create(polling))
            .Validate(Options.DefaultName, options);
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
