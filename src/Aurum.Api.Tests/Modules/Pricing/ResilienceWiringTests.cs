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
/// Calls <see cref="Program.AddPriceSource{T}"/>, the production registration, because the subject
/// is the order of two lines inside it; a hand-composed chain would only agree with itself. A swap
/// makes retries free in the ledger and billed by the provider (D-7), and nothing else would notice.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ResilienceWiringTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourceCode = "test-wiring-source";
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// The real clock, unlike every other test in this folder: Polly takes the container's
    /// <see cref="TimeProvider"/>, and a fake one would make the backoff hang rather than fail.
    /// </summary>
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
    /// Paid in real time, so 1ms unless the test is about the schedule.
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

            // Left at the production default unless a test names one.
            if (maxAttempts is { } attempts)
            {
                o.MaxAttempts = attempts;
            }
        });

        // A real governor: the lease count must be the one a provider would have billed.
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
    /// A real schedule longer than modelled (jitter on, a changed default) lets the validator approve
    /// a TotalTimeout that truncates the last attempt; the upper bounds are what catch it (D-15).
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

        public Task<HttpResponseMessage> FetchAsync(CancellationToken ct) =>
            http.GetAsync("price", ct);

        public Task<PriceQuote> GetLatestQuoteAsync(string symbol, CancellationToken ct) =>
            throw new NotSupportedException("These tests observe the handler chain, not the body.");
    }
}
