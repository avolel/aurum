using System.Diagnostics;
using System.Net;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// That the retry loop sits <em>above</em> the quota governor, against a real Postgres ledger.
/// </summary>
/// <remarks>
/// <para>
/// These call <see cref="Program.AddPriceSource{T}"/>, the production registration, on purpose. The
/// thing under test is the order of two lines inside that method; hand-composing
/// <c>new ResilienceHandler { InnerHandler = new QuotaHandler { … } }</c> re-declares that order in
/// the test and then asserts only that the test agrees with itself. <c>QuotaHandlerTests</c>' own
/// hand-composed rig is right for <em>that</em> class, where the handler is the whole subject; here
/// the wiring is.
/// </para>
/// <para>
/// A swap of those two lines makes retries free in our ledger and billed by the provider — an
/// under-count, which is the direction that costs a month rather than a poll (D-7). Nothing else in
/// the codebase would notice.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ResilienceWiringTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourceCode = "test-wiring-source";
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// The real clock, unlike every other test in this folder.
    /// </summary>
    /// <remarks>
    /// <c>AddResilienceHandler</c> resolves <see cref="TimeProvider"/> from the container and hands
    /// it to Polly's strategies, so registering a <c>FakeTimeProvider</c> here makes the retry
    /// backoff wait on a clock that nothing advances — the test hangs rather than failing. These
    /// tests assert call counts and ledger rows, neither of which needs a controlled clock, so they
    /// pay a real exponential backoff instead. Anything asserting on a quota *period* belongs in
    /// QuotaGovernorTests, which has the fake clock and no pipeline.
    /// </remarks>
    private readonly TimeProvider _clock = TimeProvider.System;

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext(_clock);
        await db.ApiQuotaWindows.Where(w => w.SourceCode == SourceCode).ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds a provider whose <c>WiringProbeSource</c> client is registered by the production
    /// path, with <paramref name="transport"/> standing in for the network.
    /// </summary>
    /// <param name="backoffBase">
    /// Polly's first retry delay. These tests pay it in real seconds (see <see cref="_clock"/>), and
    /// the production default of 2s makes a three-attempt test sleep for six — so the tests that
    /// only count calls and ledger rows run at 1ms, and the one test that is *about* the schedule
    /// sets a value it can measure. A test asserting a count must not also be asserting that Polly
    /// waits, or it pays for a guarantee it never checks.
    /// </param>
    private ServiceProvider BuildProvider(
        CountingHandler transport,
        int monthlyRequestLimit = 100,
        TimeSpan? backoffBase = null,
        int? maxAttempts = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(_clock);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(TestPriceSources.For(
            SourceCode,
            monthlyRequestLimit,
            QuotaPeriodKind.CalendarMonthUtc));

        services.AddOptions<PriceFeedResilienceOptions>().Configure(o =>
        {
            o.RetryBackoffBase = backoffBase ?? TimeSpan.FromMilliseconds(1);

            // Left at the production default unless a test names one, so the attempt count these
            // tests assert stays the shipped attempt count.
            if (maxAttempts is { } attempts)
            {
                o.MaxAttempts = attempts;
            }
        });

        // A real governor over the fixture's database. The whole point is that the lease count is
        // the one a provider would have billed, so an in-memory stand-in would test nothing.
        services.AddScoped(_ => fixture.CreateDbContext(_clock));
        services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

        Program.AddPriceSource<WiringProbeSource>(services, SourceCode, (_, _) => { });

        // Replace only the outermost thing that touches a socket. Everything above it — the
        // resilience pipeline, QuotaHandler, their order — is exactly what Program.cs builds.
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => transport));

        return services.BuildServiceProvider();
    }

    private async Task<(int Used, DateTimeOffset? ProviderRejectedAt)> LedgerAsync()
    {
        await using var db = fixture.CreateDbContext(_clock);
        var window = await db.ApiQuotaWindows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.SourceCode == SourceCode, Ct);

        return (window?.RequestsUsed ?? 0, window?.ProviderRejectedAt);
    }

    [Fact]
    public async Task Each_retry_attempt_spends_its_own_lease()
    {
        // 500 is retryable, so the pipeline makes all three attempts and gives up.
        var transport = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await using var provider = BuildProvider(transport);
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<WiringProbeSource>();

        // A 500 is a response, not an exception: the pipeline retries it, gives up, and hands the
        // last one back. Asserting a throw here would be asserting HttpClient's behaviour, not ours.
        using var response = await source.FetchAsync(Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // MaxRetryAttempts = 2, so three calls leave the process. Each one is a request the
        // provider bills, so each one must be a lease.
        Assert.Equal(3, transport.CallCount);

        var (used, _) = await LedgerAsync();
        Assert.Equal(3, used);
    }

    [Fact]
    public async Task A_provider_rejection_is_not_retried()
    {
        var transport = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await using var provider = BuildProvider(transport);
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<WiringProbeSource>();

        await Assert.ThrowsAsync<QuotaExhaustedException>(() => source.FetchAsync(Ct));

        // Exactly one. QuotaHandler converts the 429 into QuotaExhaustedException *below* the
        // retry, and the predicate does not handle that type — another attempt cannot succeed
        // until the period rolls, and QuotaHandler would charge for it either way.
        Assert.Equal(1, transport.CallCount);

        var (_, rejectedAt) = await LedgerAsync();
        Assert.NotNull(rejectedAt);
    }

    [Fact]
    public async Task A_denied_lease_never_reaches_the_network()
    {
        var transport = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Seed the window at its limit, so the next acquire is denied.
        await using (var seedProvider = BuildProvider(transport, monthlyRequestLimit: 1))
        {
            using var seedScope = seedProvider.CreateScope();
            var governor = seedScope.ServiceProvider.GetRequiredService<IQuotaGovernor>();
            Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        }

        await using var provider = BuildProvider(transport, monthlyRequestLimit: 1);
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<WiringProbeSource>();

        await Assert.ThrowsAsync<QuotaExhaustedException>(() => source.FetchAsync(Ct));

        // Zero, and the counter is unchanged. A denial must not burn budget proving it is a denial.
        Assert.Equal(0, transport.CallCount);

        var (used, _) = await LedgerAsync();
        Assert.Equal(1, used);
    }

    /// <summary>
    /// Polly's real backoff schedule matches the one
    /// <see cref="PriceSourcesOptionsValidator.MinimumTotalTimeout"/> models.
    /// </summary>
    /// <remarks>
    /// <para>That formula is a reconstruction of Polly's exponential schedule from outside Polly,
    /// and the validator refuses a <c>TotalTimeout</c> below it. If the real schedule is ever
    /// <em>longer</em> than modelled — jitter switched on, <c>BackoffType</c> changed, a package
    /// upgrade moving a default — the formula under-estimates, the validator approves a
    /// <c>TotalTimeout</c> that truncates the last attempt, and that attempt spends its lease
    /// without being able to finish. Nothing else in the codebase compares the two.</para>
    ///
    /// <para>The transport answers instantly, so each attempt contributes nothing and the elapsed
    /// span is the backoff alone — which is why the expected value is the formula evaluated at a
    /// <em>zero</em> request timeout. That isolates the half of the formula that models Polly from
    /// the half that is plain multiplication.</para>
    ///
    /// <para><b>The upper bounds are the load-bearing assertions.</b> A schedule shorter than
    /// modelled only makes the validator stricter than it needs to be, which costs a few seconds of
    /// ceiling; a schedule longer than modelled is the silent overspend. The lower bounds are there
    /// to catch the backoff being skipped altogether, which would make the whole comparison
    /// vacuous.</para>
    ///
    /// <para>This is the Polly half only. The arithmetic half is
    /// <c>PriceSourcesOptionsTests.MinimumTotalTimeout_sums_an_exponential_schedule</c>, which needs
    /// no clock, so when this fails and that passes, the retry pipeline moved and the formula did
    /// not.</para>
    ///
    /// <para>Rows cost their backoff sum in real time, about 1.6s together. Each base is large
    /// enough that the governor's Postgres round-trip between attempts is small beside the signal: a
    /// 20ms base would sit inside the noise, and the production 2s would make this the slowest test
    /// in the suite.</para>
    /// </remarks>
    [Theory]
    [InlineData(200, 3)]  // the original case: two delays, ~600ms
    [InlineData(100, 4)]  // three delays, ~700ms: a wrong multiplier cannot match all three
    [InlineData(300, 2)]  // one delay, ~300ms: the edge where linear and exponential agree
    public async Task Backoff_schedule_matches_what_MinimumTotalTimeout_models(int backoffBaseMs, int maxAttempts)
    {
        var backoffBase = TimeSpan.FromMilliseconds(backoffBaseMs);

        var transport = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await using var provider = BuildProvider(transport, backoffBase: backoffBase, maxAttempts: maxAttempts);
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<WiringProbeSource>();

        using var response = await source.FetchAsync(Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var stamps = transport.Timestamps;
        Assert.Equal(maxAttempts, stamps.Count);

        // Per-gap rather than the total only, so a failure names which delay diverged — and so a
        // linear backoff, whose first gap is right and second is half, cannot hide inside a total.
        for (var i = 0; i < maxAttempts - 1; i++)
        {
            var observed = Stopwatch.GetElapsedTime(stamps[i], stamps[i + 1]);
            var modelled = backoffBase * Math.Pow(2, i);

            Assert.InRange(
                observed,
                // 25ms of slack downward for timer granularity; the delay itself cannot be skipped.
                modelled - TimeSpan.FromMilliseconds(25),
                modelled + GapOverhead);
        }

        // The tie back to production. Evaluated at TimeSpan.Zero because the attempts themselves
        // took no measurable time here, so what remains in the formula is exactly the backoff sum.
        var expected = PriceSourcesOptionsValidator.MinimumTotalTimeout(
            TimeSpan.Zero, maxAttempts, backoffBase);

        // One GapOverhead per gap: the headroom is per inter-attempt round-trip, so a fixed
        // multiple would be too tight at four attempts and too loose at two.
        var span = Stopwatch.GetElapsedTime(stamps[0], stamps[^1]);
        Assert.InRange(span, expected - TimeSpan.FromMilliseconds(25), expected + GapOverhead * (maxAttempts - 1));
    }

    /// <summary>
    /// Headroom for one inter-attempt gap: a quota acquire against real Postgres, plus the handler
    /// dispatch either side of it. Generous on purpose — this test exists to catch a schedule that
    /// is wrong by a factor, not one that is late by a scheduler quantum.
    /// </summary>
    private static readonly TimeSpan GapOverhead = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The smallest thing that satisfies <see cref="IPriceSource"/> and exposes its
    /// <see cref="HttpClient"/>. It never parses a body, because what these tests observe is the
    /// handler chain rather than any provider's JSON.
    /// </summary>
    private sealed class WiringProbeSource(HttpClient http) : IPriceSource
    {
        public string Code => SourceCode;

        public int Priority => 1;

        public Task<HttpResponseMessage> FetchAsync(CancellationToken ct) =>
            http.GetAsync("price", ct);

        public Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct) =>
            throw new NotSupportedException("These tests observe the handler chain, not the body.");
    }
}

/// <summary>Counts what actually left the process and when, and answers from a script.</summary>
internal sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<long> _timestamps = [];
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> as each request entered, so a test can measure the gaps the
    /// retry strategy left between attempts. Raw ticks rather than <c>TimeSpan</c> because the unit
    /// only means anything through <see cref="Stopwatch.GetElapsedTime(long, long)"/>.
    /// </summary>
    public IReadOnlyList<long> Timestamps
    {
        get { lock (_gate) { return [.. _timestamps]; } }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Before respond(), so the stamp is when the attempt reached the network rather than when
        // the stub finished deciding what to say.
        lock (_gate)
        {
            _timestamps.Add(Stopwatch.GetTimestamp());
        }

        Interlocked.Increment(ref _callCount);
        return Task.FromResult(respond(request));
    }
}
