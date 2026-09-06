using System.Globalization;
using Aurum.Api.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aurum.Api.Modules.Pricing.Quota;

/// <summary>
/// Durable request budget backed by Postgres: one <see cref="ApiQuotaWindow"/> row per
/// (source, accounting period), and the row itself is the bucket.
/// </summary>
/// <remarks>
/// <para><b>Why acquire is raw SQL in an EF codebase.</b> Consuming a request is a
/// read-modify-write on a contended counter. Loading the row, deciding in C#, and saving leaves a
/// gap between the check and the increment — two callers both read 9 of 10 and both write 10, and
/// nothing in the C# can close that gap because the gap is *between* the round trips. So the check
/// and the increment are pushed into one statement (<see cref="AcquireSql"/>):
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING</c>. Postgres takes a row lock before
/// evaluating the <c>DO UPDATE</c> and re-reads the row under it, so a caller arriving mid-flight
/// tests against the updated count rather than the snapshot its transaction started with. That
/// re-read is the whole guarantee; EF's load-change-save model cannot express it.
/// </para>
/// <para>
/// A returned row means the write happened, so the lease is granted and its remaining count comes
/// straight out of <c>RETURNING</c>. No row means the condition failed. Three cases collapse into
/// the one statement: a missing period row is the <c>INSERT</c> (creating the row *is* the first
/// acquire), and both "budget spent" and "provider already rejected us" are the <c>WHERE</c>
/// failing. Nothing computes <c>Granted</c> in C# — the database's answer is the decision, which is
/// why the counter cannot drift from the lease.
/// </para>
///
/// <para><b>Contract:</b></para>
/// <list type="number">
/// <item>Budget survives process restart — a fresh instance sees the used count, not zero.</item>
/// <item>Concurrent acquires never oversubscribe: N racing callers against a budget of M grant
/// exactly min(N, M). The unique index on (SourceCode, PeriodKey) is what makes a second competing
/// row for the same period impossible, and therefore what makes <c>ON CONFLICT</c> fire.</item>
/// <item>Period rollover needs no scheduled job. A new period means a new key, which means no row,
/// which means the next acquire creates one. Spent rows are never reset — they stay as history.</item>
/// <item><see cref="ReportProviderRejectionAsync"/> clamps remaining budget to zero for the rest of
/// the period. It does so via <c>ProviderRejectedAt</c>, not by moving the counter — see the note on
/// <see cref="QuotaStatus"/> before computing remaining budget as Limit minus Used. It is an upsert
/// for the same reason acquire is: a request that straddles a period boundary is rejected against a
/// period whose row does not exist yet, and an UPDATE would drop that clamp silently.</item>
/// </list>
///
/// <para><b>Decisions, with reasoning, in <c>ops/decisions/decisions.md</c> (D-7):</b> no refunds on
/// transport failure; denials logged at Debug because the poller already logs each exhaustion
/// episode once, with the cause read back separately so the line can name it; the injected
/// <see cref="TimeProvider"/> is the sole authority for every timestamp this class writes, so
/// <c>now()</c> never appears in its SQL.</para>
/// </remarks>
public class PostgresQuotaGovernor(
    AurumDbContext db,
    TimeProvider clock,
    ILogger<PostgresQuotaGovernor> logger,
    IOptions<PriceSourcesOptions> sources) : IQuotaGovernor
{
    private const string AcquireSql = """
    INSERT INTO api_quota_windows
        ("SourceCode", "PeriodKey", "PeriodStartsAt", "PeriodEndsAt",
         "RequestLimit", "RequestsUsed", "CreatedAt", "UpdatedAt")
    VALUES (@code, @period, @startsAt, @endsAt, @limit, 1, @now, @now)
    ON CONFLICT ("SourceCode", "PeriodKey") DO UPDATE
       SET "RequestsUsed" = api_quota_windows."RequestsUsed" + 1,
           "UpdatedAt"    = @now
     WHERE api_quota_windows."RequestsUsed" < api_quota_windows."RequestLimit"
       AND api_quota_windows."ProviderRejectedAt" IS NULL
    RETURNING "RequestLimit" - "RequestsUsed";
    """;
    // An UPDATE cannot clamp a period that has no row yet, so this is an upsert too. RequestsUsed
    // is 0 on insert because that is the honest count: nothing was ever charged to this period.
    // The clamp lives in ProviderRejectedAt, never in the counter — see the QuotaStatus remarks.
    // COALESCE keeps the first rejection's timestamp; when the provider first turned us away is
    // evidence, and a later rejection in the same period must not overwrite it.
    private const string ClampSql = """
    INSERT INTO api_quota_windows
        ("SourceCode", "PeriodKey", "PeriodStartsAt", "PeriodEndsAt",
         "RequestLimit", "RequestsUsed", "ProviderRejectedAt", "CreatedAt", "UpdatedAt")
    VALUES (@code, @period, @startsAt, @endsAt, @limit, 0, @now, @now, @now)
    ON CONFLICT ("SourceCode", "PeriodKey") DO UPDATE
       SET "ProviderRejectedAt" = COALESCE(api_quota_windows."ProviderRejectedAt", @now),
           "UpdatedAt"          = @now;
    """;

    public async Task<QuotaLease> AcquireAsync(string sourceCode, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var (periodKey, periodStartsAt, periodEndsAt, requestLimit) = GetCurrentPeriod(sourceCode, now);

        var remaining = await TryConsumeAsync(sourceCode,
            periodKey,
            periodStartsAt,
            periodEndsAt,
            now,
            requestLimit, ct);

        if (remaining is not null)
            return new QuotaLease(Granted: true, Remaining: remaining.Value, ResetsAt: periodEndsAt);

        // The acquire statement collapses "budget spent" and "provider rejected" into the same
        // null, so the cause has to be read back to log it. Deliberately outside that statement,
        // and deliberately diagnostic only: a rejection could land, or the period roll, between
        // the two queries. That makes the message occasionally stale, which is fine for a log
        // line and wrong for anything that decides. Nothing below may branch on this.
        var status = await GetStatusAsync(sourceCode, ct);
        if (status.ProviderRejected)
        {
            logger.LogDebug(
                "{Source} denied: provider rejected us earlier in period {Period}; budget clamped until {ResetsAt:O}.",
                sourceCode, periodKey, periodEndsAt);
        }
        else
        {
            logger.LogDebug("{Source} denied: no budget left in period {Period} ({Used}/{Limit}).",
                sourceCode, periodKey, status.Used, status.Limit);
        }
        return new QuotaLease(Granted: false, Remaining: 0, ResetsAt: periodEndsAt);
    }

    public async Task ReportProviderRejectionAsync(string sourceCode, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var (periodKey, periodStartsAt, periodEndsAt, requestLimit) = GetCurrentPeriod(sourceCode, now);

        // ExecuteSqlRawAsync rather than TryConsumeAsync's hand-rolled command: that one only
        // opens its own connection because it needs a scalar back, and this statement has no
        // result worth reading — it has no WHERE, so it always affects exactly one row.
        await db.Database.ExecuteSqlRawAsync(ClampSql, [
            new NpgsqlParameter("code",     sourceCode),
            new NpgsqlParameter("period",   periodKey),
            new NpgsqlParameter("startsAt", periodStartsAt),
            new NpgsqlParameter("endsAt",   periodEndsAt),
            new NpgsqlParameter("limit",    requestLimit),
            new NpgsqlParameter("now",      now),
        ], ct);
    }

    public async Task<QuotaStatus> GetStatusAsync(string sourceCode, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var (periodKey, periodStartsAt, periodEndsAt, requestLimit) = GetCurrentPeriod(sourceCode, now);

        var row = await db.ApiQuotaWindows
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.SourceCode == sourceCode && w.PeriodKey == periodKey, ct);

        if (row is null)
        {
            return new QuotaStatus(
                Used: 0,
                Limit: requestLimit,
                ResetsAt: periodEndsAt,
                ProviderRejected: false);
        }

        return new QuotaStatus(
            Used: row.RequestsUsed,
            Limit: row.RequestLimit,
            ResetsAt: row.PeriodEndsAt,
            ProviderRejected: row.ProviderRejectedAt is not null);
    }

    /// <summary>
    /// Resolves the accounting period and budget for a source from that source's own
    /// configuration.
    /// </summary>
    /// <remarks>
    /// An unconfigured source throws rather than falling back to a default limit and period —
    /// see <see cref="PriceSourcesOptions.RequireByCode"/> for why there is no defensible default.
    /// </remarks>
    private (string PeriodKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int RequestLimit) GetCurrentPeriod(
        string sourceCode,
        DateTimeOffset now)
    {
        var options = sources.Value.RequireByCode(sourceCode);

        var (periodKey, startsAt, endsAt) = ResolvePeriod(options.QuotaPeriod, now, options.PeriodAnchor);
        return (periodKey, startsAt, endsAt, options.MonthlyRequestLimit);
    }

    private async Task<int?> TryConsumeAsync(
    string sourceCode, string periodKey,
    DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now, int requestLimit,
    CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = AcquireSql;

        // If EF has a transaction open, a raw command must join it - otherwise it runs on the
        // same connection but outside the transaction, silently breaking atomicity.
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        command.Parameters.Add(new NpgsqlParameter("code", sourceCode));
        command.Parameters.Add(new NpgsqlParameter("period", periodKey));
        command.Parameters.Add(new NpgsqlParameter("startsAt", startsAt));
        command.Parameters.Add(new NpgsqlParameter("endsAt", endsAt));
        command.Parameters.Add(new NpgsqlParameter("limit", requestLimit));
        command.Parameters.Add(new NpgsqlParameter("now", now));

        // EF ref-counts explicit opens, so this pairs safely with CloseConnectionAsync and is a
        // no-op when EF already had the connection open.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // ExecuteScalar returns the first column of the first row - or null when the
            // statement produced no rows at all. That null *is* the denial signal.
            return await command.ExecuteScalarAsync(ct) as int?;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Maps a moment to the provider's accounting period. Opaque to callers; only equality and
    /// the period's end instant matter.
    /// </summary>
    internal static (string PeriodKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt) ResolvePeriod(
        QuotaPeriodKind kind,
        DateTimeOffset now,
        DateTimeOffset? anchor)
    {
        var utc = now.ToUniversalTime();
        switch (kind)
        {
            case QuotaPeriodKind.CalendarMonthUtc:
                {
                    var startsAt = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
                    var endsAt = startsAt.AddMonths(1);
                    return (startsAt.ToString("yyyy-MM", CultureInfo.InvariantCulture), startsAt, endsAt);
                }
            case QuotaPeriodKind.RollingThirtyDays:
                {
                    var start = (anchor ?? throw new ArgumentNullException(
                       nameof(anchor), $"{kind} requires a per-source anchor date.")).ToUniversalTime();
                    var periodDays = 30;
                    var index = (long)Math.Floor((utc - start).TotalDays / periodDays);
                    var startsAt = start.AddDays(index * periodDays);
                    return ($"r30-{startsAt:yyyy-MM-dd}", startsAt, startsAt.AddDays(periodDays));
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown period kind");
        }
    }
}
