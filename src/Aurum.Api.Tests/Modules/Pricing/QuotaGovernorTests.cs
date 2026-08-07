using Aurum.Api.Modules.Pricing;
using Aurum.Api.Modules.Pricing.Quota;
using Aurum.Api.Shared;
using Aurum.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The contract <see cref="PostgresQuotaGovernor"/> has to satisfy. These fail today — the
/// governor is a deliberate stub.
/// </summary>
/// <remarks>
/// The first test is the one that matters. GoldAPI's free tier resets monthly, so a counter
/// that resets with the process can spend the whole month in an afternoon, and it will do it
/// quietly: every individual request looks fine.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class QuotaGovernorTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourceCode = "test-source";
    // xunit v2 has no per-test cancellation token. Not worth a framework migration in Phase 0;
    // revisit if a governor test ever hangs long enough to matter.
    private static CancellationToken Ct => CancellationToken.None;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        // Each test starts from an empty ledger; the fixture's database is shared.
        await using var db = fixture.CreateDbContext();
        await db.ApiQuotaWindows.Where(w => w.SourceCode == SourceCode).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IQuotaGovernor NewGovernor(AurumDbContext db)
        => new PostgresQuotaGovernor(db, _clock, NullLogger<PostgresQuotaGovernor>.Instance);

    /// <summary>
    /// The Phase 0 exit criterion: "quota governor demonstrably surviving a container restart."
    /// A second governor instance over a fresh DbContext stands in for the restarted process.
    /// </summary>
    [Fact]
    public async Task Budget_survives_a_restart()
    {
        const int limit = 5;
        await SeedWindowAsync(limit);

        await using (var db = fixture.CreateDbContext())
        {
            var governor = NewGovernor(db);
            for (var i = 0; i < 3; i++)
            {
                Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
            }
        }

        // Restart: new DbContext, new governor, same database.
        await using (var db = fixture.CreateDbContext())
        {
            var governor = NewGovernor(db);
            var status = await governor.GetStatusAsync(SourceCode, Ct);

            Assert.Equal(3, status.Used);
            Assert.Equal(limit, status.Limit);
        }
    }

    [Fact]
    public async Task Acquire_is_denied_once_the_budget_is_spent()
    {
        await SeedWindowAsync(requestLimit: 2);
        await using var db = fixture.CreateDbContext();
        var governor = NewGovernor(db);

        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);

        var denied = await governor.AcquireAsync(SourceCode, Ct);
        Assert.False(denied.Granted);
        Assert.Equal(0, denied.Remaining);
        Assert.True(denied.ResetsAt > _clock.GetUtcNow());
    }

    /// <summary>
    /// N racing callers against a budget of M must grant exactly min(N, M). Each gets its own
    /// DbContext because a DbContext is not thread-safe — the concurrency being tested is
    /// between database transactions, which is where the guarantee has to live.
    /// </summary>
    [Fact]
    public async Task Concurrent_acquires_never_oversubscribe()
    {
        const int limit = 10;
        const int callers = 40;
        await SeedWindowAsync(limit);

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            return await NewGovernor(db).AcquireAsync(SourceCode, Ct);
        }));

        Assert.Equal(limit, results.Count(r => r.Granted));
    }

    /// <summary>First acquire in a period must create its own counter row.</summary>
    [Fact]
    public async Task First_acquire_in_a_period_creates_the_window()
    {
        await using var db = fixture.CreateDbContext();
        var governor = NewGovernor(db);

        var lease = await governor.AcquireAsync(SourceCode, Ct);

        Assert.True(lease.Granted);
        Assert.True(await db.ApiQuotaWindows.AnyAsync(w => w.SourceCode == SourceCode, Ct));
    }

    /// <summary>
    /// Crossing a period boundary restores budget without any scheduled job — the period key
    /// changing is the whole mechanism.
    /// </summary>
    [Fact]
    public async Task Budget_returns_when_the_period_rolls()
    {
        await SeedWindowAsync(requestLimit: 1);
        await using var db = fixture.CreateDbContext();
        var governor = NewGovernor(db);

        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        Assert.False((await governor.AcquireAsync(SourceCode, Ct)).Granted);

        _clock.Advance(TimeSpan.FromDays(20)); // 2026-07-15 -> 2026-08-04

        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
    }

    /// <summary>
    /// The provider is authoritative. Our count can only ever under-count, so a 429 must zero
    /// out the rest of the period rather than just bumping the counter by one.
    /// </summary>
    [Fact]
    public async Task Provider_rejection_clamps_the_remaining_budget()
    {
        await SeedWindowAsync(requestLimit: 100);
        await using var db = fixture.CreateDbContext();
        var governor = NewGovernor(db);

        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        await governor.ReportProviderRejectionAsync(SourceCode, Ct);

        Assert.False((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        Assert.True((await governor.GetStatusAsync(SourceCode, Ct)).ProviderRejected);
    }

    [Theory]
    [InlineData(QuotaPeriodKind.CalendarMonthUtc, "2026-07-15T12:00:00Z", "2026-08-01T00:00:00Z")]
    [InlineData(QuotaPeriodKind.CalendarMonthUtc, "2026-12-31T23:59:59Z", "2027-01-01T00:00:00Z")]
    public void ResolvePeriod_ends_the_period_at_the_expected_boundary(
        QuotaPeriodKind kind, string now, string expectedEnd)
    {
        var (_, _, endsAt) = PostgresQuotaGovernor.ResolvePeriod(kind, DateTimeOffset.Parse(now), anchor: null);
        Assert.Equal(DateTimeOffset.Parse(expectedEnd), endsAt);
    }

    private async Task SeedWindowAsync(int requestLimit)
    {
        var (periodKey, startsAt, endsAt) =
            PostgresQuotaGovernor.ResolvePeriod(QuotaPeriodKind.CalendarMonthUtc, _clock.GetUtcNow(), anchor: null);

        await using var db = fixture.CreateDbContext();
        db.ApiQuotaWindows.Add(new ApiQuotaWindow
        {
            SourceCode = SourceCode,
            PeriodKey = periodKey,
            PeriodStartsAt = startsAt,
            PeriodEndsAt = endsAt,
            RequestLimit = requestLimit,
            RequestsUsed = 0,
        });
        await db.SaveChangesAsync();
    }
}
