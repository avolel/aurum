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
/// These pin two defects that were invisible because both failed by succeeding. The governor
/// resolved a source's budget through a switch with a fall-through arm, so an unconfigured source
/// was accounted against a hardcoded 100. And <c>ValidateDataAnnotations()</c> does not descend
/// into nested objects, so the annotations on a source were never evaluated — a missing API key
/// bound to the empty string and the process booted clean.
/// </remarks>
public class PriceSourcesOptionsTests
{
    /// <summary>
    /// A fitting cadence unless the test sets one, so each test fails on the rule it names
    /// rather than on the missing polling section.
    /// </summary>
    private static Dictionary<string, string?> WithPolling(Dictionary<string, string?> values)
    {
        // 24h = 31 polls in 31 days, and 93 requests at the default MaxAttempts of 3 — under the
        // 100-request default limit these tests inherit. It was 12h while the budget guard counted
        // polls; 62 polls became 186 requests the moment it started counting attempts, so the
        // cadence that existed to keep tests off this rule was the first thing to trip it.
        values.TryAdd("PricePolling:PollInterval", "1.00:00:00");
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

        Assert.Equal("goldapi.io", options.RequireByCode("GoldAPI.IO").SourceCode);
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
            ["PricePolling:PollInterval"] = "00:05:00",
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
            ["PriceSources:Second:Priority"] = "2",
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
            ["PriceSources:ApiNinjas:SourceCode"] = "api-ninjas",
            ["PriceSources:ApiNinjas:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:ApiNinjas:ApiKey"] = "key",
            ["PriceSources:ApiNinjas:Priority"] = "2",
        });

        Assert.False(options.RequireByCode("goldapi.io").Enabled);
    }

    /// <summary>
    /// A source wired up in code but missing from config used to boot clean. Its first poll then
    /// reached RequireByCode inside the HTTP handler, where the poller swallowed the error and
    /// retried forever.
    /// </summary>
    [Fact]
    public void A_registered_source_without_a_config_entry_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(
            new()
            {
                ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
                ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
                ["PriceSources:GoldApiIo:ApiKey"] = "key",
            },
            GoldApiIoSource.SourceCode,
            ApiNinjasSource.SourceCode);

        Assert.Contains($"'{ApiNinjasSource.SourceCode}' is registered", failure.Message, StringComparison.Ordinal);
        // Only the missing entry is reported, not every registered source.
        Assert.DoesNotContain($"'{GoldApiIoSource.SourceCode}' is registered", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check is about whether an entry exists, not whether the source is enabled. Disabling an
    /// entry is how you park a provider, so a disabled entry must satisfy its registration.
    /// </summary>
    [Fact]
    public void A_registered_source_with_a_disabled_entry_boots()
    {
        var options = ValidatedOptions(
            new()
            {
                ["PriceSources:ApiNinjas:SourceCode"] = ApiNinjasSource.SourceCode,
                ["PriceSources:ApiNinjas:Enabled"] = "false",
                ["PriceSources:GoldApiIo:SourceCode"] = GoldApiIoSource.SourceCode,
                ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
                ["PriceSources:GoldApiIo:ApiKey"] = "key",
                ["PriceSources:GoldApiIo:Priority"] = "2",
            },
            ApiNinjasSource.SourceCode);

        Assert.False(options.RequireByCode(ApiNinjasSource.SourceCode).Enabled);
    }

    /// <summary>
    /// The rule that replaced "every source must cover every poll". A backup is called only while
    /// the sources above it are down, so holding it to the full period would peg the feed's cadence
    /// to the smallest budget in the file. It may run out mid-outage; the governor stops it cleanly.
    /// </summary>
    [Fact]
    public void A_backup_that_cannot_cover_the_period_still_boots()
    {
        var options = ValidatedOptions(new()
        {
            ["PricePolling:PollInterval"] = "00:15:00",
            ["PriceSources:ApiNinjas:SourceCode"] = "api-ninjas",
            ["PriceSources:ApiNinjas:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:ApiNinjas:ApiKey"] = "key",
            ["PriceSources:ApiNinjas:Priority"] = "1",
            ["PriceSources:ApiNinjas:MonthlyRequestLimit"] = "10000",   // 2,976 polls fit
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:Priority"] = "2",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",     // 2,976 polls do not
        });

        // Pins the premise. If someone raises the cadence above, the backup starts fitting and
        // this test passes without testing anything. TimeSpan / TimeSpan returns a double ratio.
        Assert.True(TimeSpan.FromDays(31) / TimeSpan.FromMinutes(15) > 100);
        Assert.True(options.RequireByCode("goldapi.io").Enabled);
    }

    /// <summary>
    /// The main source is the lowest Priority among enabled entries, not whichever says 1. Parking
    /// the top provider must hand the strict check to the next one; a hardcoded Priority == 1 would
    /// keep checking the disabled entry's generous budget and let the real primary overspend.
    /// </summary>
    [Fact]
    public void Disabling_the_top_source_promotes_the_next_one()
    {
        var failure = ValidationFailure(new()
        {
            ["PricePolling:PollInterval"] = "00:15:00",
            ["PriceSources:ApiNinjas:SourceCode"] = "api-ninjas",
            ["PriceSources:ApiNinjas:Priority"] = "1",
            ["PriceSources:ApiNinjas:Enabled"] = "false",
            ["PriceSources:ApiNinjas:MonthlyRequestLimit"] = "10000",   // would pass, if it were checked
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",  // complete, so the only
            ["PriceSources:GoldApiIo:ApiKey"] = "key",                         // possible failure is budget
            ["PriceSources:GoldApiIo:Priority"] = "2",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
        });

        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PriceSources:ApiNinjas", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Priority is the failover order. Two enabled entries tied for first means the source serving
    /// every healthy poll — and the one whose budget the boot check guarantees — is decided by DI
    /// registration order, which nobody chose.
    /// </summary>
    [Fact]
    public void Enabled_sources_may_not_share_the_lowest_priority()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:Priority"] = "1",
            ["PriceSources:ApiNinjas:SourceCode"] = "api-ninjas",
            ["PriceSources:ApiNinjas:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:ApiNinjas:ApiKey"] = "key",
            ["PriceSources:ApiNinjas:Priority"] = "1",
        });

        // Both keys, so the operator sees which entries to reorder without searching the file.
        Assert.Contains("PriceSources:GoldApiIo", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PriceSources:ApiNinjas", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With every source switched off the poller has nothing to call. It used to log one warning
    /// and exit while the API reported healthy and never served a price.
    /// </summary>
    [Fact]
    public void A_configuration_with_no_enabled_source_fails_the_host_at_boot()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:Enabled"] = "false",
            ["PriceSources:ApiNinjas:SourceCode"] = "api-ninjas",
            ["PriceSources:ApiNinjas:Enabled"] = "false",
        });

        // Match this to the wording you give the failure.
        Assert.Contains("enabled", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// TotalTimeout bounds the whole retry sequence and RequestTimeout one attempt, so a TotalTimeout
    /// that does not exceed RequestTimeout cancels every retry before it opens a socket. The retry
    /// strategy is then configured, reads as configured, and does nothing — the same failure
    /// HttpClient.Timeout caused before the per-attempt budget moved inside the pipeline.
    /// </summary>
    [Theory]
    [InlineData("00:00:10")]  // equal: the first attempt consumes the entire total budget
    [InlineData("00:00:05")]  // smaller: the first attempt is cut short by the outer strategy
    public void TotalTimeout_must_exceed_RequestTimeout(string totalTimeout)
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:00:10",
            ["PriceSources:GoldApiIo:TotalTimeout"] = totalTimeout,
        });

        Assert.Contains("TotalTimeout", failure.Message, StringComparison.Ordinal);
        Assert.Contains("RequestTimeout", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retry sequence that can outlive the cadence puts two polls in flight at once. The poller
    /// assumes one, and the budget guard above counts one request per tick — so overlapping ticks
    /// spend quota at a rate nothing in the configuration accounts for.
    /// </summary>
    [Fact]
    public void TotalTimeout_may_not_reach_the_poll_interval()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:01:00",
            ["PriceSources:GoldApiIo:TotalTimeout"] = "00:06:00",
            // Deliberately not the WithPolling default: the cadence is the rule under test.
            ["PricePolling:PollInterval"] = "00:05:00",
        });

        Assert.Contains("TotalTimeout", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A poll is not a request. Every attempt re-enters QuotaHandler and is charged its own lease,
    /// so the cadence guard has to multiply by MaxAttempts.
    /// </summary>
    /// <remarks>
    /// This is the defect item 4 exists to close: while the guard counted polls it approved a
    /// cadence the primary's budget could not fund, under-counting worst-case spend threefold.
    /// The circuit breaker does not bound it — SourceCircuit only observes poll outcomes, so a
    /// provider that fails twice and succeeds on the third attempt sustains the full multiple
    /// indefinitely while looking perfectly healthy.
    /// </remarks>
    [Fact]
    public void Poll_budget_counts_every_attempt_not_every_poll()
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            // 62 polls in 31 days — inside the limit. 186 requests at 3 attempts each is not.
            ["PricePolling:PollInterval"] = "12:00:00",
            ["PriceFeed:Resilience:MaxAttempts"] = "3",
        });

        Assert.Contains("MaxAttempts", failure.Message, StringComparison.Ordinal);
        Assert.Contains("186", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same cadence and budget pass with retries switched off, which is what pins the failure
    /// above to the multiplier rather than to the cadence.
    /// </summary>
    [Fact]
    public void Poll_budget_accepts_the_same_cadence_when_a_poll_spends_one_request()
    {
        ValidatedOptions(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:MonthlyRequestLimit"] = "100",
            ["PricePolling:PollInterval"] = "12:00:00",
            ["PriceFeed:Resilience:MaxAttempts"] = "1",
        });
    }

    /// <summary>
    /// TotalTimeout has to fit every attempt plus every backoff delay, or the last attempt is
    /// cancelled part-way and MaxAttempts reads as one number while behaving as another.
    /// </summary>
    /// <remarks>
    /// Both values here are real mistakes rather than invented ones. 30s is
    /// <c>MaxAttempts × RequestTimeout</c> with the backoff forgotten entirely; 35s is that same
    /// arithmetic with a few seconds added by eye, and it was the shipped default until this check
    /// existed to reject it. A truncated attempt still spends its lease, so the cost is silent.
    /// </remarks>
    [Theory]
    [InlineData("00:00:30")]  // 3 × RequestTimeout exactly: no room for either backoff delay
    [InlineData("00:00:35")]  // the old default: 2s + 4s of backoff unaccounted for, needs 36s
    public void TotalTimeout_must_fit_every_attempt_and_its_backoff(string totalTimeout)
    {
        var failure = ValidationFailure(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
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
    /// The same 35s is legal with retries switched off: the requirement scales with MaxAttempts
    /// rather than being a new floor on TotalTimeout.
    /// </summary>
    [Fact]
    public void TotalTimeout_needs_no_backoff_room_when_there_are_no_retries()
    {
        ValidatedOptions(new()
        {
            ["PriceSources:GoldApiIo:SourceCode"] = "goldapi.io",
            ["PriceSources:GoldApiIo:BaseUrl"] = "https://example.invalid/",
            ["PriceSources:GoldApiIo:ApiKey"] = "key",
            ["PriceSources:GoldApiIo:RequestTimeout"] = "00:00:10",
            ["PriceSources:GoldApiIo:TotalTimeout"] = "00:00:35",
            ["PriceFeed:Resilience:MaxAttempts"] = "1",
        });
    }

    /// <summary>
    /// The formula alone, with no clock and no pipeline, so a failure here means the arithmetic
    /// moved rather than Polly.
    /// </summary>
    /// <remarks>
    /// <para>Every <paramref name="expectedSeconds"/> is worked by hand. Computing it in the test as
    /// a loop over <c>base × 2^i</c> would re-implement the method and assert that it agrees with
    /// itself — the trap <c>ResilienceWiringTests</c> avoids by calling
    /// <c>Program.AddPriceSource</c>.</para>
    ///
    /// <para>The rows are chosen to separate formulas that coincide on the shipped case. At one
    /// backoff delay a linear and an exponential schedule are identical, and at two a doubled
    /// multiplier can still land on a plausible total; four delays leave no room for either.</para>
    ///
    /// <para><c>Backoff_schedule_matches_what_MinimumTotalTimeout_models</c> is the other half: it
    /// measures Polly and cannot see this arithmetic, and this cannot see Polly.</para>
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
    /// The defaults a service gets when there is no <c>PriceFeed:Resilience</c> section at all.
    /// </summary>
    /// <remarks>
    /// Every retry test in <c>ResilienceWiringTests</c> overrides <c>RetryBackoffBase</c> to keep
    /// the suite fast, so a default that silently became zero would pass all of them while
    /// production lost its backoff. <c>ShippedConfigurationTests</c> reads <c>appsettings.json</c>,
    /// which does not reach the property initialisers this pins.
    /// </remarks>
    [Fact]
    public void Resilience_defaults_are_the_shipped_attempt_count_and_backoff()
    {
        var defaults = new PriceFeedResilienceOptions();

        Assert.Equal(3, defaults.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RetryBackoffBase);
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

    private static PriceSourcesOptions ValidatedOptions(
        Dictionary<string, string?> values, params string[] registeredCodes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(WithPolling(values)).Build();
        var polling = config.GetSection(PricePollingOptions.SectionName).Get<PricePollingOptions>()!;

        var options = Bind(values);
        var registered = registeredCodes.Select(code => new RegisteredPriceSource(code));
        var result = new PriceSourcesOptionsValidator(registered, Options.Create(polling), Resilience(config))
            .Validate(Options.DefaultName, options);

        Assert.False(result.Failed, result.FailureMessage);
        return options;
    }

    /// <summary>
    /// The resilience knobs from configuration, falling back to the production defaults.
    /// </summary>
    /// <remarks>
    /// The fallback is load-bearing: <c>Get&lt;T&gt;()</c> returns null for a section that is
    /// absent, and most tests here never set one. Binding null would hand the validator a
    /// <c>MaxAttempts</c> of zero — which its own guard treats as "another validator owns this
    /// message" and skips the budget check for, so every test would pass the rule vacuously.
    /// </remarks>
    private static IOptions<PriceFeedResilienceOptions> Resilience(IConfiguration config) =>
        Options.Create(
            config.GetSection(PriceFeedResilienceOptions.SectionName).Get<PriceFeedResilienceOptions>()
            ?? new PriceFeedResilienceOptions());

    /// <summary>
    /// Runs validation the way the host does — through ValidateOnStart — so a failure here proves
    /// the process would refuse to boot, not merely that the validator returns a failure when
    /// called directly.
    /// </summary>
    /// <para>Pass <paramref name="registeredCodes"/> to stand in for PricingModule's per-source
    /// registrations. With none, the container resolves an empty sequence, so the registered-source
    /// check passes vacuously and the other checks are tested in isolation.</para>
    private static OptionsValidationException ValidationFailure(
        Dictionary<string, string?> values, params string[] registeredCodes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(WithPolling(values)).Build();

        var services = new ServiceCollection();

        services.AddOptions<PricePollingOptions>()
            .BindConfiguration(PricePollingOptions.SectionName);

        // Mirrors Program.cs. BindConfiguration binds onto a fresh instance, so an absent section
        // leaves the production defaults in place rather than nulling them the way Get<T>() would.
        services.AddOptions<PriceFeedResilienceOptions>()
            .BindConfiguration(PriceFeedResilienceOptions.SectionName);

        foreach (var code in registeredCodes)
        {
            services.AddSingleton(new RegisteredPriceSource(code));
        }

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
