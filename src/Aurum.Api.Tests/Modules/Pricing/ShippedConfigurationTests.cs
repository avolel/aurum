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
/// Every other test builds configuration in memory, which tests the rules and not the file; that
/// gap once shipped a cadence the primary could not fund (D-10). Keys are supplied by an override
/// layer standing in for <c>.env</c>, because the file ships them empty on purpose (D-9).
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
        using var host = BuildHost(
            GoldApiIoSource.SourceCode, ApiNinjasSource.SourceCode, MetalPriceApiSource.SourceCode);

        // Throws the boot failure, with its message, if the shipped file does not validate.
        _ = host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;
    }

    /// <summary>
    /// The rule the shipped file once broke: fails if someone lowers <c>PollInterval</c> or
    /// reorders <c>Priority</c> without checking the two together.
    /// </summary>
    [Fact]
    public void Shipped_primary_budget_funds_the_shipped_cadence()
    {
        using var host = BuildHost();
        var options = host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;
        var polling = host.GetRequiredService<IOptions<PricePollingOptions>>().Value;
        var resilience = host.GetRequiredService<IOptions<PriceFeedResilienceOptions>>().Value;

        var primary = options.Sources.Values
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .First();

        // 31 days is the worst case: the most polls a period can hold.
        var pollsPerPeriod = PriceSourcesOptionsValidator.LongestPeriod / polling.PollInterval;

        // Every attempt is charged its own lease (D-15).
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
    /// All three sources once shipped 35s, leaving out the 2s + 4s of backoff. Asserted against the
    /// validator's own formula rather than a literal, so the two cannot drift.
    /// </remarks>
    [Fact]
    public void Shipped_total_timeouts_fit_the_shipped_attempt_count()
    {
        using var host = BuildHost();
        var options = host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;
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
    /// The validator enforces this at boot; naming the codes here makes a source added without
    /// configuration fail in CI rather than at startup.
    /// </summary>
    [Fact]
    public void Shipped_appsettings_configures_every_registered_source()
    {
        using var host = BuildHost();
        var options = host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;

        Assert.NotNull(options.RequireByCode(GoldApiIoSource.SourceCode));
        Assert.NotNull(options.RequireByCode(ApiNinjasSource.SourceCode));
        Assert.NotNull(options.RequireByCode(MetalPriceApiSource.SourceCode));
    }

    /// <summary>
    /// The shipped buffer must hold one day plus slack at the shipped cadence, or the 1d window
    /// never has a starting price.
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
    /// The shipped thresholds pass the boot rules, cover every window, and keep BR-02's one fixed
    /// number: a 0.25% move over 5 minutes.
    /// </summary>
    [Fact]
    public void Shipped_significance_thresholds_are_valid_and_match_BR02()
    {
        var options = BuildConfiguration().GetSection(SignificanceOptions.SectionName).Get<SignificanceOptions>()!;

        Assert.True(SignificanceOptions.WindowKeysAreKnown(options));
        Assert.True(SignificanceOptions.ValuesAreInRange(options));
        Assert.False(string.IsNullOrWhiteSpace(options.ThresholdProfile));
        Assert.Equal(DeltaWindow.All.Count, options.Windows.Count);
        Assert.Equal(0.25m, options.Windows[DeltaWindow.FiveMinutes.Code].MinPercent);
    }

    /// <summary>
    /// The shipped file, then the credentials an operator supplies through <c>.env</c>. The
    /// in-memory layer comes second so it overrides the empty keys.
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

    private static ServiceProvider BuildHost(params string[] registeredCodes) =>
        PricingOptionsHost.Build(BuildConfiguration(), registeredCodes);
}
