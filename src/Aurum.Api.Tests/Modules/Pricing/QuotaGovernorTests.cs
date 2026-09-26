using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Data;
using Aurum.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The contract <see cref="PostgresQuotaGovernor"/> has to satisfy.
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
    // xunit v2 has no per-test cancellation token. Not worth a framework migration for this;
    // revisit if a governor test ever hangs long enough to matter.
    private static CancellationToken Ct => CancellationToken.None;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<PriceSourcesOptions> _sources = TestPriceSources.For(SourceCode);

    public async Task InitializeAsync()
    {
        // Each test starts from an empty ledger; the fixture's database is shared.
        await using var db = fixture.CreateDbContext();
        await db.ApiQuotaWindows.Where(w => w.SourceCode == SourceCode).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IQuotaGovernor NewGovernor(AurumDbContext db)
        => new PostgresQuotaGovernor(db, _clock, NullLogger<PostgresQuotaGovernor>.Instance, _sources);

    /// <summary>
    /// The governor's first contract clause: budget survives a process restart, so a fresh
    /// instance sees the used count rather than zero. A second governor over a fresh DbContext
    /// stands in for the restarted process.
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

    /// <summary>
    /// A rejection must clamp the period even when no row exists for it yet — an UPDATE cannot
    /// change a row that is not there, so the clamp used to vanish silently.
    /// </summary>
    /// <remarks>
    /// Reachable when a request straddles a period boundary: the acquire counts against the old
    /// period, the response lands in the new one, and the rejection is written against a period
    /// key nothing has touched. The seeded-window case is already covered by
    /// <see cref="Provider_rejection_clamps_the_remaining_budget"/>; this one deliberately seeds
    /// nothing. <c>Used == 0</c> is the honest count — no request was ever charged to this
    /// period — and a row reading "0 used, rejected" is the loudest drift signal the ledger has.
    /// </remarks>
    [Fact]
    public async Task Rejection_before_any_acquire_creates_a_clamped_window()
    {
        await using var db = fixture.CreateDbContext(_clock);
        var governor = NewGovernor(db);

        await governor.ReportProviderRejectionAsync(SourceCode, Ct);

        var status = await governor.GetStatusAsync(SourceCode, Ct);
        Assert.True(status.ProviderRejected);
        Assert.Equal(0, status.Used);
        Assert.False((await governor.AcquireAsync(SourceCode, Ct)).Granted);
    }

    /// <summary>
    /// D-7's "known wart", now fixed: the acquire statement cannot tell a spent budget from a
    /// provider rejection, so the denial log asserted the first regardless. The two need
    /// different operator responses — one waits out the period, the other means our count is
    /// drifting from the provider's — so the line has to name the cause it actually found.
    /// </summary>
    [Fact]
    public async Task Denial_after_provider_rejection_names_the_provider()
    {
        await SeedWindowAsync(requestLimit: 100);
        await using var db = fixture.CreateDbContext(_clock);
        var logger = new ListLogger<PostgresQuotaGovernor>();
        var governor = new PostgresQuotaGovernor(db, _clock, logger, _sources);

        await governor.ReportProviderRejectionAsync(SourceCode, Ct);
        Assert.False((await governor.AcquireAsync(SourceCode, Ct)).Granted);

        var denial = Assert.Single(logger.Entries, e => e.Message.Contains("denied"));
        Assert.Contains("rejected", denial.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no budget left", denial.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the pair: a spent budget must not be reported as a provider rejection.
    /// Without this, a denial branch that always said "rejected" would pass the test above.
    /// </summary>
    [Fact]
    public async Task Denial_on_a_spent_budget_does_not_blame_the_provider()
    {
        await SeedWindowAsync(requestLimit: 1);
        await using var db = fixture.CreateDbContext(_clock);
        var logger = new ListLogger<PostgresQuotaGovernor>();
        var governor = new PostgresQuotaGovernor(db, _clock, logger, _sources);

        Assert.True((await governor.AcquireAsync(SourceCode, Ct)).Granted);
        Assert.False((await governor.AcquireAsync(SourceCode, Ct)).Granted);

        var denial = Assert.Single(logger.Entries, e => e.Message.Contains("denied"));
        Assert.Contains("no budget left", denial.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rejected", denial.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// D-7, "Authoritative clock": the injected <see cref="TimeProvider"/> is the sole authority
    /// for anything this table records.
    /// </summary>
    /// <remarks>
    /// The governor's raw SQL already binds every timestamp from the clock, but rows written
    /// through EF get their audit stamps from <c>AurumDbContext.ApplyAuditFields</c>. While that
    /// reads <c>DateTimeOffset.UtcNow</c>, two rows in the same table carry timestamps from two
    /// different clocks — invisible in production, a month apart under a fake one.
    /// </remarks>
    [Fact]
    public async Task Audit_timestamps_come_from_the_injected_clock()
    {
        await SeedWindowAsync(requestLimit: 100);

        await using var db = fixture.CreateDbContext(_clock);
        var row = await db.ApiQuotaWindows.AsNoTracking().SingleAsync(w => w.SourceCode == SourceCode);

        Assert.Equal(_clock.GetUtcNow(), row.CreatedAt);
        Assert.Equal(_clock.GetUtcNow(), row.UpdatedAt);
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

        await using var db = fixture.CreateDbContext(_clock);
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
