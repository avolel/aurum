using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// What configuration binding and validation owe the rest of the pricing module.
/// </summary>
/// <remarks>
/// Pins two defects that failed by succeeding: a switch with a fall-through arm that accounted an
/// unconfigured source against a hardcoded 100, and <c>ValidateDataAnnotations()</c> not
/// descending into nested objects, so a missing API key booted clean.
/// </remarks>
public class PriceSourcesOptionsTests
{
    private static readonly (string Key, string Code) GoldApi = ("GoldApiIo", GoldApiIoSource.SourceCode);
    private static readonly (string Key, string Code) ApiNinjas = ("ApiNinjas", ApiNinjasSource.SourceCode);

    /// <summary>
    /// A fitting cadence unless the test sets one, so each test fails on the rule it names
    /// rather than on the missing polling section.
    /// </summary>
    private static Dictionary<string, string?> WithPolling(Dictionary<string, string?> values)
    {
        // 24h = 31 polls in 31 days, 93 requests at the default MaxAttempts of 3: under the
        // 100-request default limit these tests inherit.
        values.TryAdd("PricePolling:PollInterval", "1.00:00:00");
        return values;
    }

    /// <summary>SourceCode, BaseUrl and ApiKey for each entry: enough to pass the credential checks.</summary>
    private static Dictionary<string, string?> Usable(params (string Key, string Code)[] sources)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, code) in sources)
        {
            values[$"PriceSources:{key}:SourceCode"] = code;
            values[$"PriceSources:{key}:BaseUrl"] = "https://example.invalid/";
            values[$"PriceSources:{key}:ApiKey"] = "key";
        }

        return values;
    }

    /// <summary>
    /// The whole point of the map: two sources, two budgets, no shared default.
    /// </summary>
    [Fact]
    public void Each_source_gets_its_own_configured_limit()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PriceSources:Frugal:SourceCode"] = "frugal.example",
            ["PriceSources:Frugal:MonthlyRequestLimit"] = "20",
        });

        Assert.Equal(100, options.RequireByCode(GoldApiIoSource.SourceCode).MonthlyRequestLimit);
        Assert.Equal(20, options.RequireByCode("frugal.example").MonthlyRequestLimit);
    }

    /// <summary>
    /// An unconfigured source must fail rather than inherit someone else's budget.
    /// </summary>
    [Fact]
    public void An_unconfigured_source_throws_rather_than_defaulting()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => options.RequireByCode("someone.else"));
        Assert.Contains("someone.else", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Configuration keys are case-insensitive, so lookup by source code has to be too.
    /// </summary>
    [Fact]
    public void Lookup_by_code_is_case_insensitive()
    {
        var options = Bind(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
        });

        Assert.Equal(
            GoldApiIoSource.SourceCode,
            options.RequireByCode(GoldApiIoSource.SourceCode.ToUpperInvariant()).SourceCode);
    }

    /// <summary>
    /// <c>[Required]</c> on ApiKey never fired while ValidateDataAnnotations() was the only check.
    /// </summary>
    [Fact]
    public void A_missing_api_key_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:ApiKey"] = "",
        });

        Assert.Contains("ApiKey", failure.Message, StringComparison.Ordinal);
        // The path, not just the property, so the operator knows which entry to edit.
        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cadence that cannot fit its budget used to be caught by the poller, after the host had
    /// already reported healthy.
    /// </summary>
    [Fact]
    public void A_cadence_that_overspends_its_budget_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PricePolling:PollInterval"] = "00:05:00",
        });

        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without an anchor, RollingThirtyDays throws on every acquire inside an HTTP handler, which
    /// the poller swallows and retries forever.
    /// </summary>
    [Fact]
    public void A_rolling_period_without_an_anchor_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:QuotaPeriod"] = "RollingThirtyDays",
        });

        Assert.Contains("PeriodAnchor", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two entries sharing a source code would collide on one ledger row and share one budget.
    /// </summary>
    [Fact]
    public void Duplicate_source_codes_fail_the_host_at_boot()
    {
        var failure = ValidationFailure(new(Usable(
            ("First", GoldApiIoSource.SourceCode),
            ("Second", GoldApiIoSource.SourceCode)))
        {
            ["PriceSources:Second:Priority"] = "2",
        });

        Assert.Contains("already declared", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disabled source is never charged, so it may sit in the file half-configured.
    /// </summary>
    [Fact]
    public void A_disabled_source_is_not_held_to_its_credentials()
    {
        var options = ValidatedOptions(new(Usable(ApiNinjas))
        {
            ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
            ["PriceSources:GoldApiIo:Enabled"] = "false",
            ["PriceSources:ApiNinjas:Priority"] = "2",
        });

        Assert.False(options.RequireByCode(GoldApiIoSource.SourceCode).Enabled);
    }

    /// <summary>
    /// A source registered in code but missing from config used to boot clean, then fail inside
    /// the HTTP handler on every poll.
    /// </summary>
    [Fact]
    public void A_registered_source_without_a_config_entry_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(
            Usable(GoldApi),
            GoldApiIoSource.SourceCode,
            ApiNinjasSource.SourceCode);

        Assert.Contains($"'{ApiNinjasSource.SourceCode}' is registered", failure.Message, StringComparison.Ordinal);
        // Only the missing entry is reported, not every registered source.
        Assert.DoesNotContain($"'{GoldApiIoSource.SourceCode}' is registered", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check is whether an entry exists, not whether it is enabled: disabling is how a
    /// provider is parked.
    /// </summary>
    [Fact]
    public void A_registered_source_with_a_disabled_entry_boots()
    {
        var options = ValidatedOptions(
            new(Usable(GoldApi))
            {
                ["PriceSources:ApiNinjas:SourceCode"] = ApiNinjasSource.SourceCode,
                ["PriceSources:ApiNinjas:Enabled"] = "false",
                ["PriceSources:GoldApiIo:Priority"] = "2",
            },
            ApiNinjasSource.SourceCode);

        Assert.False(options.RequireByCode(ApiNinjasSource.SourceCode).Enabled);
    }

    /// <summary>
    /// Backups are exempt from the full-period check (D-10); holding them to it would peg the
    /// cadence to the smallest budget in the file.
    /// </summary>
    [Fact]
    public void A_backup_that_cannot_cover_the_period_still_boots()
    {
        var options = ValidatedOptions(new(Usable(ApiNinjas, GoldApi))
        {
            ["PricePolling:PollInterval"] = "00:15:00",
            ["PriceSources:ApiNinjas:Priority"] = "1",
            ["PriceSources:ApiNinjas:MonthlyRequestLimit"] = "10000",   // 2,976 polls fit
            ["PriceSources:GoldApiIo:Priority"] = "2",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",     // 2,976 polls do not
        });

        // Pins the premise: if the cadence above changes so the backup fits, this tests nothing.
        Assert.True(TimeSpan.FromDays(31) / TimeSpan.FromMinutes(15) > 100);
        Assert.True(options.RequireByCode(GoldApiIoSource.SourceCode).Enabled);
    }

    /// <summary>
    /// The primary is the lowest Priority among enabled entries, not whichever says 1, so parking
    /// the top provider hands the strict check to the next one.
    /// </summary>
    [Fact]
    public void Disabling_the_top_source_promotes_the_next_one()
    {
        // GoldApiIo is complete, so the only possible failure is its budget.
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PricePolling:PollInterval"] = "00:15:00",
            ["PriceSources:ApiNinjas:SourceCode"] = ApiNinjasSource.SourceCode,
            ["PriceSources:ApiNinjas:Priority"] = "1",
            ["PriceSources:ApiNinjas:Enabled"] = "false",
            ["PriceSources:ApiNinjas:MonthlyRequestLimit"] = "10000",   // would pass, if it were checked
            ["PriceSources:GoldApiIo:Priority"] = "2",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
        });

        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PriceSources:ApiNinjas", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tie for first would leave the primary, and the budget the boot check guarantees, to DI
    /// registration order.
    /// </summary>
    [Fact]
    public void Enabled_sources_may_not_share_the_lowest_priority()
    {
        var failure = ValidationFailure(new(Usable(GoldApi, ApiNinjas))
        {
            ["PriceSources:GoldApiIo:Priority"] = "1",
            ["PriceSources:ApiNinjas:Priority"] = "1",
        });

        // Both keys, so the operator sees which entries to reorder without searching the file.
        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PriceSources:ApiNinjas", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With nothing enabled the poller used to log one warning and exit while the API reported
    /// healthy.
    /// </summary>
    [Fact]
    public void A_configuration_with_no_enabled_source_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
            ["PriceSources:GoldApiIo:Enabled"] = "false",
            ["PriceSources:ApiNinjas:SourceCode"] = ApiNinjasSource.SourceCode,
            ["PriceSources:ApiNinjas:Enabled"] = "false",
        });

        Assert.Contains("enabled", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A TotalTimeout at or below RequestTimeout cancels every retry before it opens a socket.
    /// </summary>
    [Theory]
    [InlineData("00:00:10")]  // equal: the first attempt consumes the entire total budget
    [InlineData("00:00:05")]  // smaller: the first attempt is cut short by the outer strategy
    public void TotalTimeout_must_exceed_RequestTimeout(string totalTimeout)
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:00:10",
            ["PriceSources:GoldApiIo:TotalTimeout"] = totalTimeout,
        });

        Assert.Contains("TotalTimeout", failure.Message, StringComparison.Ordinal);
        Assert.Contains("RequestTimeout", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retry sequence that outlives the cadence puts two polls in flight, which the budget guard
    /// does not account for.
    /// </summary>
    [Fact]
    public void TotalTimeout_may_not_reach_the_poll_interval()
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:01:00",
            ["PriceSources:GoldApiIo:TotalTimeout"] = "00:06:00",
            // Deliberately not the WithPolling default: the cadence is the rule under test.
            ["PricePolling:PollInterval"] = "00:05:00",
        });

        Assert.Contains("TotalTimeout", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every attempt is charged its own lease, so the cadence guard multiplies by MaxAttempts
    /// (D-15). The circuit breaker does not bound this multiple.
    /// </summary>
    [Fact]
    public void Poll_budget_counts_every_attempt_not_every_poll()
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            // 62 polls in 31 days — inside the limit. 186 requests at 3 attempts each is not.
            ["PricePolling:PollInterval"] = "12:00:00",
            ["PriceFeed:Resilience:MaxAttempts"] = "3",
        });

        Assert.Contains("MaxAttempts", failure.Message, StringComparison.Ordinal);
        Assert.Contains("186", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same cadence passes with retries off, which pins the failure above to the multiplier.
    /// </summary>
    [Fact]
    public void Poll_budget_accepts_the_same_cadence_when_a_poll_spends_one_request()
    {
        ValidatedOptions(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PricePolling:PollInterval"] = "12:00:00",
            ["PriceFeed:Resilience:MaxAttempts"] = "1",
        });
    }

    /// <summary>
    /// TotalTimeout has to fit every attempt plus every backoff delay, or the last attempt is cut
    /// short and still spends its lease.
    /// </summary>
    [Theory]
    [InlineData("00:00:30")]  // 3 × RequestTimeout exactly: no room for either backoff delay
    [InlineData("00:00:35")]  // the old default: 2s + 4s of backoff unaccounted for, needs 36s
    public void TotalTimeout_must_fit_every_attempt_and_its_backoff(string totalTimeout)
    {
        var failure = ValidationFailure(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:00:10",
            ["PriceSources:GoldApiIo:TotalTimeout"] = totalTimeout,
            ["PriceFeed:Resilience:MaxAttempts"] = "3",
            ["PriceFeed:Resilience:RetryBackoffBase"] = "00:00:02",
        });

        Assert.Contains("TotalTimeout", failure.Message, StringComparison.Ordinal);
        Assert.Contains("MaxAttempts", failure.Message, StringComparison.Ordinal);
        Assert.Contains("backoff", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same 35s is legal with retries off: the requirement scales with MaxAttempts.
    /// </summary>
    [Fact]
    public void TotalTimeout_needs_no_backoff_room_when_there_are_no_retries()
    {
        ValidatedOptions(new(Usable(GoldApi))
        {
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:00:10",
            ["PriceSources:GoldApiIo:TotalTimeout"] = "00:00:35",
            ["PriceFeed:Resilience:MaxAttempts"] = "1",
        });
    }

    /// <summary>
    /// The formula alone, no clock and no pipeline. The other half is
    /// <c>Backoff_schedule_matches_what_MinimumTotalTimeout_models</c>, which measures Polly.
    /// </summary>
    /// <remarks>
    /// Expectations are worked by hand; a <c>base × 2^i</c> loop here would re-implement the method.
    /// The rows separate formulas that agree on the shipped case.
    /// </remarks>
    [Theory]
    [InlineData(10, 3, 2, 36)]  // shipped settings: 30s of attempts + 2s + 4s — why 35s was rejected
    [InlineData(10, 1, 2, 10)]  // no retries, so no backoff at all: the MaxAttempts - 1 edge
    [InlineData(10, 2, 2, 22)]  // one delay only: linear agrees here, which is why 3 attempts is also needed
    [InlineData(10, 5, 1, 65)]  // 1 + 2 + 4 + 8 = 15s of backoff: catches a wrong multiplier
    [InlineData(10, 3, 0, 30)]  // zero base must neither throw nor go negative
    public void MinimumTotalTimeout_sums_an_exponential_schedule(
        int requestTimeoutSeconds, int maxAttempts, int backoffBaseSeconds, int expectedSeconds)
    {
        var actual = PriceSourcesOptionsValidator.MinimumTotalTimeout(
            TimeSpan.FromSeconds(requestTimeoutSeconds),
            maxAttempts,
            TimeSpan.FromSeconds(backoffBaseSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), actual);
    }

    /// <summary>
    /// Every retry test overrides <c>RetryBackoffBase</c> to stay fast, so this is the only test
    /// that notices the production default changing.
    /// </summary>
    [Fact]
    public void Resilience_defaults_are_the_shipped_attempt_count_and_backoff()
    {
        var defaults = new PriceFeedResilienceOptions();

        Assert.Equal(3, defaults.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RetryBackoffBase);
    }

    /// <summary>Binds the way <c>Program.cs</c> does, through the config paths compose sets.</summary>
    private static PriceSourcesOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new PriceSourcesOptions();
        config.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources);
        return options;
    }

    private static PriceSourcesOptions ValidatedOptions(
        Dictionary<string, string?> values, params string[] registeredCodes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(WithPolling(values)).Build();
        using var host = PricingOptionsHost.Build(config, registeredCodes);

        return host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value;
    }

    /// <summary>
    /// Validates the way the host does, so a failure proves the process would refuse to boot.
    /// With no <paramref name="registeredCodes"/> the registered-source check passes vacuously.
    /// </summary>
    private static OptionsValidationException ValidationFailure(
        Dictionary<string, string?> values, params string[] registeredCodes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(WithPolling(values)).Build();
        using var host = PricingOptionsHost.Build(config, registeredCodes);

        return Assert.Throws<OptionsValidationException>(
            () => host.GetRequiredService<IOptions<PriceSourcesOptions>>().Value);
    }
}
