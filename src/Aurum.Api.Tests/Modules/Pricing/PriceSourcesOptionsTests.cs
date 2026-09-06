using Aurum.Api.Modules.Pricing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// What configuration binding and validation owe the rest of the pricing module.
/// </summary>
/// <remarks>
/// These pin two defects that were invisible because both failed by succeeding. The governor
/// resolved a source's budget through a switch with a fall-through arm, so an unconfigured source
/// was accounted against a hardcoded 100. And <c>ValidateDataAnnotations()</c> does not descend
/// into nested objects, so the annotations on a source were never evaluated — a missing API key
/// bound to the empty string and the process booted clean.
/// </remarks>
public class PriceSourcesOptionsTests
{
    /// <summary>
    /// The whole point of the map: two sources, two budgets, no shared default.
    /// </summary>
    [Fact]
    public void Each_source_gets_its_own_configured_limit()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PriceSources:Frugal:SourceCode"] = "frugal.example",
            ["PriceSources:Frugal:MonthlyRequestLimit"] = "20",
        });

        Assert.Equal(100, options.RequireByCode("goldapi.io").MonthlyRequestLimit);
        Assert.Equal(20, options.RequireByCode("frugal.example").MonthlyRequestLimit);
    }

    /// <summary>
    /// An unconfigured source must fail rather than inherit someone else's budget. The old
    /// fall-through granted it 100 requests a month and a calendar-month period regardless of what
    /// the provider actually allows.
    /// </summary>
    [Fact]
    public void An_unconfigured_source_throws_rather_than_defaulting()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => options.RequireByCode("someone.else"));
        Assert.Contains("someone.else", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Configuration keys are case-insensitive, so lookup by source code has to be too — a map
    /// with the default ordinal comparer would miss keys the config system treats as identical.
    /// </summary>
    [Fact]
    public void Lookup_by_code_is_case_insensitive()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
        });

        Assert.True(options.TryGetByCode("GoldAPI.IO", out _));
    }

    /// <summary>
    /// The defect in plain form: <c>[Required]</c> on a source's ApiKey never fired, because
    /// ValidateDataAnnotations() stops at the properties of the object it was given.
    /// </summary>
    [Fact]
    public void A_missing_api_key_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "",
        });

        Assert.Contains("ApiKey", failure.Message, StringComparison.Ordinal);
        // The path, not just the property, so the operator knows which entry to edit.
        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cadence that cannot fit its budget used to be caught by the poller, after the host had
    /// already reported healthy. Boot is the honest place for it.
    /// </summary>
    [Fact]
    public void A_cadence_that_overspends_its_budget_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PriceSources:GoldApiIo:PollInterval"] = "00:05:00",
        });

        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// RollingThirtyDays without an anchor throws inside the acquire path, which is inside an
    /// HTTP handler, which the poller swallows and retries forever. Refuse it at boot instead.
    /// </summary>
    [Fact]
    public void A_rolling_period_without_an_anchor_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:QuotaPeriod"] = "RollingThirtyDays",
        });

        Assert.Contains("PeriodAnchor", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two entries sharing a source code would collide on one ledger row, so the second would
    /// spend the first's budget — exactly the accounting failure the module exists to prevent.
    /// </summary>
    [Fact]
    public void Duplicate_source_codes_fail_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:First:SourceCode"] = "goldapi.io",
            ["PriceSources:First:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:First:ApiKey"] = "key",
            ["PriceSources:Second:SourceCode"] = "goldapi.io",
            ["PriceSources:Second:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:Second:ApiKey"] = "key",
        });

        Assert.Contains("already declared", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source switched off is not polled and never charged, so holding it to credential and
    /// cadence requirements would make a half-configured provider impossible to park in the file.
    /// </summary>
    [Fact]
    public void A_disabled_source_is_not_held_to_its_credentials()
    {
        var options = ValidatedOptions(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:Enabled"] = "false",
        });

        Assert.False(options.RequireByCode("goldapi.io").Enabled);
    }

    /// <summary>
    /// Mirrors PricingModule's binding so these tests exercise the real config paths — the ones
    /// docker-compose.yml and .env actually set — rather than an object graph built by hand.
    /// </summary>
    private static PriceSourcesOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new PriceSourcesOptions();
        config.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources);
        return options;
    }

    private static PriceSourcesOptions ValidatedOptions(Dictionary<string, string?> values)
    {
        var options = Bind(values);
        var result = new PriceSourcesOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Failed, result.FailureMessage);
        return options;
    }

    /// <summary>
    /// Runs validation the way the host does — through ValidateOnStart — so a failure here proves
    /// the process would refuse to boot, not merely that the validator returns a failure when
    /// called directly.
    /// </summary>
    private static OptionsValidationException ValidationFailure(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions<PriceSourcesOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources))
            .ValidateDataAnnotations();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();

        using var provider = services.BuildServiceProvider();

        return Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PriceSourcesOptions>>().Value);
    }
}
