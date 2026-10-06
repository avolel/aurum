using System.Net;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.Infrastructure.Data;
using Aurum.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// What the HTTP boundary owes the ledger: two D-7 decisions enforced by nothing but the shape of
/// the code.
/// </summary>
/// <remarks>
/// Driven through a real <see cref="HttpClient"/>, which does its own exception handling, to prove
/// <see cref="QuotaExhaustedException"/> actually reaches the poller's catch clause.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class QuotaHandlerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourceCode = "test-handler-source";
    private static CancellationToken Ct => CancellationToken.None;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext(_clock);
        await db.ApiQuotaWindows.Where(w => w.SourceCode == SourceCode).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// D-7, "Refunds on transport failure": a lease taken before a request that then fails in
    /// transit stays spent. An over-count costs one poll; an under-count can cost the month.
    /// </summary>
    /// <remarks>
    /// The executable form of a decision otherwise enforced by the absence of a catch block.
    /// </remarks>
    [Fact]
    public async Task Transport_failure_still_spends_the_lease()
    {
        using var client = NewClient(new CountingHandler(_ => throw new HttpRequestException("connection reset")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid/price", Ct));

        Assert.Equal(1, await UsedAsync());
    }

    /// <summary>
    /// A provider 429 clamps the rest of the period and surfaces as the exception the poller
    /// sleeps on.
    /// </summary>
    [Fact]
    public async Task Provider_rejection_surfaces_as_QuotaExhausted()
    {
        var content = new DisposalTrackingContent();
        using var client = NewClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = content,
        }));

        await Assert.ThrowsAsync<QuotaExhaustedException>(() => client.GetAsync("https://example.invalid/price", Ct));

        await using var db = fixture.CreateDbContext(_clock);
        var row = await db.ApiQuotaWindows.AsNoTracking().SingleAsync(w => w.SourceCode == SourceCode, Ct);
        Assert.NotNull(row.ProviderRejectedAt);

        // The handler throws instead of returning, so nothing downstream can dispose the response.
        Assert.True(content.Disposed);
    }

    private HttpClient NewClient(HttpMessageHandler inner)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AurumDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IQuotaGovernor>(sp => new PostgresQuotaGovernor(
            sp.GetRequiredService<AurumDbContext>(), _clock, NullLogger<PostgresQuotaGovernor>.Instance,
            TestPriceSources.For(SourceCode)));

        var provider = services.BuildServiceProvider();

        // As in Program.AddPriceSource: a scope per request, because IHttpClientFactory pools
        // handlers far longer than a DbContext lives.
        var handler = new QuotaHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            SourceCode,
            NullLogger<QuotaHandler>.Instance)
        {
            InnerHandler = inner,
        };

        return new HttpClient(handler);
    }

    private async Task<int> UsedAsync()
    {
        await using var db = fixture.CreateDbContext(_clock);
        var row = await db.ApiQuotaWindows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.SourceCode == SourceCode, Ct);
        return row?.RequestsUsed ?? 0;
    }

    /// <summary>Content that records whether the handler disposed the response it discarded.</summary>
    private sealed class DisposalTrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
