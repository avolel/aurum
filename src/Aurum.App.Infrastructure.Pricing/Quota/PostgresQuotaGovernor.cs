using System.Globalization;
using Aurum.App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aurum.App.Infrastructure.Pricing.Quota;

/// <summary>
/// Durable request budget backed by Postgres: one <see cref="ApiQuotaWindow"/> row per
/// (source, accounting period), and the row itself is the bucket.
/// </summary>
/// <remarks>
/// <para>Do not rewrite acquire in EF. Load, decide, save leaves a gap between check and increment.
/// <see cref="AcquireSql"/> does both in one <c>INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING</c>:
/// Postgres re-reads the row under a lock, which is the whole concurrency guarantee (D-7). The unique
/// index on (SourceCode, PeriodKey) is what makes <c>ON CONFLICT</c> fire.</para>
/// <para>A returned row is a grant; no row is a denial (spent, or provider rejected). A new period
/// is just a new key, so rollover needs no job. Timestamps come from the injected clock, never
/// <c>now()</c>.</para>
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
    // An upsert, because the period may have no row yet (D-7). RequestsUsed stays the honest
    // count; the clamp is ProviderRejectedAt. COALESCE keeps the first rejection's time.
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

        // Read back only to log the cause. Outside the atomic statement, so it can be stale:
        // nothing may branch on it (D-7).
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

        // No result to read back, so ExecuteSqlRawAsync is enough here.
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
    /// Resolves the accounting period and budget from the source's own configuration (D-9).
    /// </summary>
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

        // Join EF's open transaction, or this runs outside it on the same connection.
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        command.Parameters.Add(new NpgsqlParameter("code", sourceCode));
        command.Parameters.Add(new NpgsqlParameter("period", periodKey));
        command.Parameters.Add(new NpgsqlParameter("startsAt", startsAt));
        command.Parameters.Add(new NpgsqlParameter("endsAt", endsAt));
        command.Parameters.Add(new NpgsqlParameter("limit", requestLimit));
        command.Parameters.Add(new NpgsqlParameter("now", now));

        // EF counts explicit opens, so this pairs safely with CloseConnectionAsync.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // Null when no row came back: that is the denial.
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
