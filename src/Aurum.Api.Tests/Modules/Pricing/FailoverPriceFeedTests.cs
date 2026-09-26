using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>The chain: ordering, the Enabled filter, the circuit gate and the outcome accounting.</summary>
public class FailoverPriceFeedTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Primary = "api-ninjas";
    private const string Secondary = "goldapi.io";
    private const string Tertiary = "metalpriceapi.com";

    private static (FailoverPriceFeed Feed, SourceCircuitStore Circuits, FakeTimeProvider Clock) Build(
        IOptions<PriceSourcesOptions> options,
        IEnumerable<IPriceSource> sources,
        PriceFeedCircuitOptions? circuitOptions = null)
    {
        var clock = new FakeTimeProvider(Start);

        var circuits = new SourceCircuitStore(
            options.Value.Sources.Values.Select(s => new RegisteredPriceSource(s.SourceCode)),
            clock,
            Options.Create(circuitOptions ?? new PriceFeedCircuitOptions()));

        var feed = new FailoverPriceFeed(
            sources, options, circuits, new ListLogger<FailoverPriceFeed>());

        return (feed, circuits, clock);
    }

    [Fact]
    public async Task The_primary_serves_the_poll_and_the_backups_are_never_called()
    {
        var primary = ScriptedPriceSource.Succeeding(Primary, 1);
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup]);

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        Assert.Equal(Primary, result.Quote.SourceCode);
        Assert.False(result.UsedFallback);
        Assert.Equal(0, backup.CallCount);
    }

    [Fact]
    public async Task A_fault_fails_over_to_the_next_source()
    {
        var primary = ScriptedPriceSource.Faulting(Primary, 1, "HTTP 503 Service Unavailable.");
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup]);

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        Assert.Equal(Secondary, result.Quote.SourceCode);
        Assert.True(result.UsedFallback);
        Assert.Equal(Primary, result.PrimarySourceCode);
        Assert.Equal([Primary, Secondary], result.AttemptedSources);

        var faulted = result.Attempts.Single(a => a.SourceCode == Primary);
        Assert.Equal(SourceAttemptOutcome.Faulted, faulted.Outcome);
        Assert.Contains("503", faulted.FailureReason);
    }

    [Fact]
    public async Task Quota_exhaustion_moves_on_immediately_and_does_not_open_the_circuit()
    {
        var resetsAt = Start.AddDays(20);
        var primary = ScriptedPriceSource.OutOfQuota(Primary, 1, resetsAt);
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, circuits, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup],
            new PriceFeedCircuitOptions { FailureThreshold = 1 });

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        Assert.Equal(Secondary, result.Quote.SourceCode);

        var attempt = result.Attempts.Single(a => a.SourceCode == Primary);
        Assert.Equal(SourceAttemptOutcome.QuotaExhausted, attempt.Outcome);
        Assert.Equal(resetsAt, attempt.QuotaResetsAt);

        // Being out of budget is not a fault. Opening on it would keep a healthy source out of the
        // chain after its period rolls — and at threshold 1 a fault would have opened here.
        Assert.False(circuits.For(Primary).IsOpen());
    }

    [Fact]
    public async Task Open_circuit_source_is_not_called()
    {
        var primary = ScriptedPriceSource.Faulting(Primary, 1);
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, circuits, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup],
            new PriceFeedCircuitOptions { FailureThreshold = 1 });

        await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);
        Assert.True(circuits.For(Primary).IsOpen());
        Assert.Equal(1, primary.CallCount);

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        // CallCount, not just "the quote came from the backup". A source that is called and then
        // ignored still spends a lease, so the quote's origin alone would pass on a broken gate.
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(
            SourceAttemptOutcome.SkippedCircuitOpen,
            result.Attempts.Single(a => a.SourceCode == Primary).Outcome);

        // A skipped source produced no new evidence, so it is not part of what the poll spent.
        Assert.Equal([Secondary], result.AttemptedSources);
    }

    [Fact]
    public async Task A_disabled_source_is_not_in_the_chain()
    {
        var disabled = ScriptedPriceSource.Succeeding(Primary, 1);
        var enabled = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, false), (Secondary, 2, true)),
            [disabled, enabled]);

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        // The live bug before item 3: PricePollingService selected straight off
        // IEnumerable<IPriceSource>, which has no Enabled to filter on, so setting
        // PriceSources__ApiNinjas__Enabled=false shortened the startup log and changed nothing.
        Assert.Equal(0, disabled.CallCount);
        Assert.Equal(Secondary, result.Quote.SourceCode);

        // And it is the head of the chain now, so it is not a fallback.
        Assert.Equal(Secondary, result.PrimarySourceCode);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task Chain_order_matches_the_startup_coverage_log()
    {
        var options = TestPriceSources.ForChain(
            (Tertiary, 3, true), (Primary, 1, true), (Secondary, 2, true));

        var sources = new[]
        {
            // Registration order deliberately unrelated to priority: DI order must not decide it.
            ScriptedPriceSource.Faulting(Tertiary, 3),
            ScriptedPriceSource.Faulting(Secondary, 2),
            ScriptedPriceSource.Faulting(Primary, 1),
        };

        var (feed, _, _) = Build(options, sources);

        var thrown = await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));

        var chainOrder = thrown.Attempts.Select(a => a.SourceCode).ToList();
        var logOrder = options.Value.EnabledInFailoverOrder().Select(s => s.SourceCode).ToList();

        // Two OrderBys over the same data in two files drift. They are one method now, and this is
        // the pin that keeps them one: the coverage log names the primary that actually runs.
        Assert.Equal(logOrder, chainOrder);
        Assert.Equal([Primary, Secondary, Tertiary], chainOrder);
    }

    [Fact]
    public void Ties_below_the_primary_are_broken_deterministically()
    {
        // A tie for the LOWEST priority is refused at boot. Ties further down are allowed, so the
        // order has to come from somewhere other than dictionary enumeration.
        var first = TestPriceSources.ForChain(
            (Primary, 1, true), (Tertiary, 2, true), (Secondary, 2, true));

        var second = TestPriceSources.ForChain(
            (Secondary, 2, true), (Primary, 1, true), (Tertiary, 2, true));

        Assert.Equal(
            first.Value.EnabledInFailoverOrder().Select(s => s.SourceCode),
            second.Value.EnabledInFailoverOrder().Select(s => s.SourceCode));

        // Ordinal on the source code: "goldapi.io" before "metalpriceapi.com".
        Assert.Equal(
            [Primary, Secondary, Tertiary],
            first.Value.EnabledInFailoverOrder().Select(s => s.SourceCode));
    }

    [Fact]
    public async Task Every_source_out_of_quota_reports_the_earliest_reset()
    {
        var soon = Start.AddHours(1);
        var later = Start.AddDays(30);

        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [
                ScriptedPriceSource.OutOfQuota(Primary, 1, later),
                ScriptedPriceSource.OutOfQuota(Secondary, 2, soon),
            ]);

        var thrown = await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));

        Assert.True(thrown.AllQuotaExhausted);

        // Earliest, not last-seen. Sleeping to the latest reset idles a provider whose budget
        // refreshes in an hour for the better part of a month.
        Assert.Equal(soon, thrown.EarliestResetsAt);
    }

    [Fact]
    public async Task One_fault_among_the_quota_failures_is_not_AllQuotaExhausted()
    {
        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [
                ScriptedPriceSource.OutOfQuota(Primary, 1, Start.AddDays(20)),
                ScriptedPriceSource.Faulting(Secondary, 2),
            ]);

        var thrown = await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));

        // A fault clears on its own schedule. If this were true the poller would sleep for up to a
        // month over a provider that is likely back in minutes.
        Assert.False(thrown.AllQuotaExhausted);
    }

    [Fact]
    public async Task An_open_circuit_among_the_quota_failures_is_not_AllQuotaExhausted()
    {
        var (feed, circuits, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [
                ScriptedPriceSource.Faulting(Primary, 1),
                ScriptedPriceSource.OutOfQuota(Secondary, 2, Start.AddDays(20)),
            ],
            new PriceFeedCircuitOptions { FailureThreshold = 1 });

        await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));
        Assert.True(circuits.For(Primary).IsOpen());

        var thrown = await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));

        Assert.Contains(thrown.Attempts, a => a.Outcome == SourceAttemptOutcome.SkippedCircuitOpen);
        Assert.False(thrown.AllQuotaExhausted);
    }

    [Fact]
    public async Task An_invalid_symbol_is_not_failed_over()
    {
        var primary = ScriptedPriceSource.Throwing(
            Primary, 1, () => new ArgumentException("Expected a 6-character symbol like XAUUSD."));
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, circuits, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup],
            new PriceFeedCircuitOptions { FailureThreshold = 1 });

        await Assert.ThrowsAsync<ArgumentException>(
            () => feed.GetLatestQuoteAsync("NOPE", CancellationToken.None));

        // No source can serve a symbol that is not a symbol, so trying them all spends a lease per
        // provider to reach the same answer — and it is not the provider's fault, so no circuit.
        Assert.Equal(0, backup.CallCount);
        Assert.False(circuits.For(Primary).IsOpen());
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_fault_rather_than_the_end_of_the_chain()
    {
        var primary = ScriptedPriceSource.Throwing(
            Primary, 1, () => new InvalidOperationException("a defect in one source"));
        var backup = ScriptedPriceSource.Succeeding(Secondary, 2);

        var (feed, circuits, _) = Build(
            TestPriceSources.ForChain((Primary, 1, true), (Secondary, 2, true)),
            [primary, backup],
            new PriceFeedCircuitOptions { FailureThreshold = 1 });

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        // A bug in one provider's parsing must not take down a chain that exists for availability.
        Assert.Equal(Secondary, result.Quote.SourceCode);
        Assert.True(circuits.For(Primary).IsOpen());
    }

    [Fact]
    public async Task A_source_registered_but_absent_from_configuration_is_not_in_the_chain()
    {
        var configured = ScriptedPriceSource.Succeeding(Secondary, 2);
        var unconfigured = ScriptedPriceSource.Succeeding("never-configured", 1);

        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Secondary, 2, true)),
            [unconfigured, configured]);

        var result = await feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None);

        // Configuration is authoritative. A source with no entry has no budget, no period kind and
        // no priority we could honour, and inventing them is what D-9 refuses.
        Assert.Equal(0, unconfigured.CallCount);
        Assert.Equal(Secondary, result.Quote.SourceCode);
    }

    [Fact]
    public async Task No_enabled_source_is_a_total_failure_but_not_a_quota_failure()
    {
        var (feed, _, _) = Build(
            TestPriceSources.ForChain((Primary, 1, false)),
            [ScriptedPriceSource.Succeeding(Primary, 1)]);

        var thrown = await Assert.ThrowsAsync<AllSourcesFailedException>(
            () => feed.GetLatestQuoteAsync(SupportedSymbol.Gold, CancellationToken.None));

        Assert.Empty(thrown.Attempts);

        // An empty chain must not report "every source is out of quota" — that would put the
        // poller to sleep until a reset that no attempt ever supplied.
        Assert.False(thrown.AllQuotaExhausted);
        Assert.Null(thrown.EarliestResetsAt);
    }
}
