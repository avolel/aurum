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
    private ServiceProvider BuildProvider(CountingHandler transport, int monthlyRequestLimit = 100)
    {
        var services = new ServiceCollection();

        services.AddSingleton(_clock);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(TestPriceSources.For(
            SourceCode,
            monthlyRequestLimit,
            QuotaPeriodKind.CalendarMonthUtc));

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

/// <summary>Counts what actually left the process, and answers from a script.</summary>
internal sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(respond(request));
    }
}
