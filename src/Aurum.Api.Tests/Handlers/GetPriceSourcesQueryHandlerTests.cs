using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.Prices.DTOs;
using Aurum.App.Application.Prices.Handlers.Queries;
using Aurum.App.Application.Prices.Queries;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Aurum.Api.Tests.Handlers;

/// <remarks>
/// Real Postgres for the <c>price_sources</c> rows; a real circuit store; a mocked governor,
/// whose SQL has its own tests. The rows are seeded by migration, so this class resets their
/// projection columns instead of deleting them.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class GetPriceSourcesQueryHandlerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string GoldApi = GoldApiIoSource.SourceCode;
    private const string ApiNinjas = ApiNinjasSource.SourceCode;
    private const string MetalPrice = MetalPriceApiSource.SourceCode;

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResetsAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly Mock<IQuotaGovernor> _quota = new();

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.PriceSources.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.LastSuccessAt, (DateTimeOffset?)null)
            .SetProperty(r => r.LastFailureAt, (DateTimeOffset?)null)
            .SetProperty(r => r.LastFailureReason, (string?)null));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private SourceCircuitStore Circuits(params string[] registered) =>
        new(registered.Select(code => new RegisteredPriceSource(code)), _clock, Options.Create(new PriceFeedCircuitOptions()));

    private async Task<IReadOnlyList<PriceSourceStatusDto>> HandleAsync(
        IOptions<PriceSourcesOptions> settings, SourceCircuitStore circuits)
    {
        await using var db = fixture.CreateDbContext();

        return await new GetPriceSourcesQueryHandler(settings, db, circuits, _quota.Object)
            .Handle(new GetPriceSourcesQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task Handle_ListsEveryConfiguredSourceByPriority_IncludingSwitchedOffOnes()
    {
        var settings = TestPriceSources.ForChain((MetalPrice, 3, true), (GoldApi, 1, false), (ApiNinjas, 2, true));

        var sources = await HandleAsync(settings, Circuits(GoldApi, ApiNinjas, MetalPrice));

        Assert.Equal([GoldApi, ApiNinjas, MetalPrice], sources.Select(s => s.SourceCode));
        Assert.Equal([1, 2, 3], sources.Select(s => s.Priority));
        Assert.Equal([false, true, true], sources.Select(s => s.Enabled));
        Assert.Equal("GoldAPI.io", sources[0].DisplayName); // from the seeded row
        Assert.Equal(100, sources[0].MonthlyRequestLimit);
    }

    [Fact]
    public async Task Handle_SwitchedOffSource_IsNotAskedForItsQuota()
    {
        var settings = TestPriceSources.ForChain((GoldApi, 1, false), (ApiNinjas, 2, true));

        var sources = await HandleAsync(settings, Circuits(GoldApi, ApiNinjas));

        // A switched-off source may be half-configured, and GetStatusAsync would throw for it.
        Assert.Null(sources[0].Quota);
        Assert.NotNull(sources[1].Quota);
        _quota.Verify(q => q.GetStatusAsync(GoldApi, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RemainingIsLimitMinusUsed()
    {
        _quota.Setup(q => q.GetStatusAsync(GoldApi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotaStatus(Used: 40, Limit: 100, ResetsAt, ProviderRejected: false));

        var quota = (await HandleAsync(TestPriceSources.ForChain((GoldApi, 1, true)), Circuits(GoldApi)))[0].Quota;

        Assert.Equal(new QuotaStatusDto(Used: 40, Limit: 100, Remaining: 60, ResetsAt, ProviderRejected: false), quota);
    }

    [Fact]
    public async Task Handle_ProviderRejected_ReportsZeroRemainingAndKeepsTheRealCount()
    {
        // "40 used of 100, rejected" is the drift signal (D-7): remaining is 0, not 60.
        _quota.Setup(q => q.GetStatusAsync(GoldApi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotaStatus(Used: 40, Limit: 100, ResetsAt, ProviderRejected: true));

        var quota = (await HandleAsync(TestPriceSources.ForChain((GoldApi, 1, true)), Circuits(GoldApi)))[0].Quota;

        Assert.Equal(new QuotaStatusDto(Used: 40, Limit: 100, Remaining: 0, ResetsAt, ProviderRejected: true), quota);
    }

    [Fact]
    public async Task Handle_JoinsTheStoredRowAndTheCircuitBySourceCode()
    {
        await using (var db = fixture.CreateDbContext())
        {
            await db.PriceSources.Where(s => s.Code == ApiNinjas).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LastSuccessAt, Now.AddHours(-2))
                .SetProperty(r => r.LastFailureAt, Now.AddMinutes(-1))
                .SetProperty(r => r.LastFailureReason, "HTTP 503"));
        }

        var circuits = Circuits(GoldApi, ApiNinjas);
        for (var i = 0; i < new PriceFeedCircuitOptions().FailureThreshold; i++)
        {
            circuits.For(ApiNinjas).RecordFailure("HTTP 503");
        }

        var sources = await HandleAsync(TestPriceSources.ForChain((GoldApi, 1, true), (ApiNinjas, 2, true)), circuits);

        var ninjas = sources[1];
        Assert.Equal(Now.AddHours(-2), ninjas.LastSuccessAt);
        Assert.Equal(Now.AddMinutes(-1), ninjas.LastFailureAt);
        Assert.Equal("HTTP 503", ninjas.LastFailureReason);
        Assert.Equal(new CircuitStatusDto(IsOpen: true, ConsecutiveFailures: 3, OpenedUntil: Now.AddMinutes(15)), ninjas.Circuit);

        // The other source's row and circuit are untouched: the join is by code, not by position.
        Assert.Null(sources[0].LastFailureAt);
        Assert.Equal(new CircuitStatusDto(IsOpen: false, ConsecutiveFailures: 0, OpenedUntil: null), sources[0].Circuit);
    }

    [Fact]
    public async Task Handle_ConfiguredSourceWithNoRegisteredCode_HasNoCircuit()
    {
        var sources = await HandleAsync(
            TestPriceSources.ForChain((GoldApi, 1, true), (ApiNinjas, 2, true)), Circuits(GoldApi));

        Assert.NotNull(sources[0].Circuit);
        Assert.Null(sources[1].Circuit);
    }
}
