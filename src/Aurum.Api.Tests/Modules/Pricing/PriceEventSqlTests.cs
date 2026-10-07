using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.EntityFrameworkCore;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The waiting period and the unique rule on <c>price_events</c>, both enforced by the insert (D-18).
/// </summary>
/// <remarks>
/// Each insert uses a fresh <c>DbContext</c>, as a restarted process would. Nothing is held in memory
/// between them, which is the point.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PriceEventSqlTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset TenAm = PostgresFixture.RecentMinute;
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.PriceEvents.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PriceEvent Event(DateTimeOffset windowEndedAt, decimal? volatility = 0.1m) => new()
    {
        Symbol = SupportedSymbol.Gold,
        DetectedAt = windowEndedAt,
        WindowCode = DeltaWindow.OneDay.Code,
        WindowStartedAt = windowEndedAt - OneDay,
        WindowEndedAt = windowEndedAt,
        Direction = PriceEventDirection.Up,
        StartMid = 4_000m,
        EndMid = 4_048m,
        DeltaAbsolute = 48m,
        DeltaPercent = 1.2m,
        VelocityPercentPerMinute = 0.000833m,
        Volatility = volatility,
        SampleCount = 96,
        SourceCode = ApiNinjasSource.SourceCode,
        BaselineSourceCode = ApiNinjasSource.SourceCode,
        IsCrossSource = false,
        ThresholdProfile = "test-v1",
        TriggeredRule = "magnitude >= 1.00%",
    };

    private async Task<bool> InsertAsync(PriceEvent priceEvent, TimeSpan cooldown)
    {
        await using var db = fixture.CreateDbContext();
        return await PriceEventSql.InsertAsync(db, priceEvent, cooldown, priceEvent.DetectedAt, Ct);
    }

    private async Task<int> CountAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.PriceEvents.CountAsync(Ct);
    }

    [Fact]
    public async Task Restart_does_not_re_emit_the_same_event()
    {
        Assert.True(await InsertAsync(Event(TenAm), OneDay));
        Assert.False(await InsertAsync(Event(TenAm), OneDay));

        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// The case the in-memory waiting period missed: after a restart the next poll measures the same
    /// daily move with a newer end price, so the unique rule alone does not match.
    /// </summary>
    [Fact]
    public async Task Restart_does_not_re_emit_within_the_cooldown()
    {
        Assert.True(await InsertAsync(Event(TenAm), OneDay));
        Assert.False(await InsertAsync(Event(TenAm.AddMinutes(45)), OneDay));

        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// Exactly one waiting period later counts as after it: the check is strictly "ended later than
    /// end minus cooldown".
    /// </summary>
    [Fact]
    public async Task Event_after_the_cooldown_is_saved()
    {
        Assert.True(await InsertAsync(Event(TenAm), TimeSpan.FromHours(1)));
        Assert.True(await InsertAsync(Event(TenAm.AddHours(1)), TimeSpan.FromHours(1)));

        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task Event_without_volatility_is_saved_with_null()
    {
        Assert.True(await InsertAsync(Event(TenAm, volatility: null), OneDay));

        await using var db = fixture.CreateDbContext();
        var saved = await db.PriceEvents.AsNoTracking().SingleAsync(Ct);
        Assert.Null(saved.Volatility);
        Assert.Equal(TenAm, saved.CreatedAt);
    }
}
