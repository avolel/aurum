using Aurum.App.Infrastructure.Data.Entities.Logging;

namespace Aurum.App.Application.AppLogs;

/// <inheritdoc />
public sealed class AppLogService<T>(
    IAppLogQueue queue,
    IRequestContextAccessor context,
    TimeProvider clock) : IAppLogService<T>
{
    /// <summary>
    /// Matches <c>app_logs.details</c>. Truncated here rather than at SaveChanges: the drain
    /// batches rows, so one oversized row would fail 22001 for the whole batch and lose the other
    /// entries with it.
    /// </summary>
    private const int MaxDetailsLength = 4000;

    private const int MaxStackTraceLength = 8000;

    public Task LogAsync(
        LogType logType,
        string action,
        string? details = null,
        int? statusCode = null,
        long? durationMs = null,
        CancellationToken ct = default)
    {
        Enqueue(entry =>
        {
            entry.LogType = logType.ToString();
            entry.Action = action;
            entry.Details = Truncate(details, MaxDetailsLength);
            entry.StatusCode = statusCode;
            entry.DurationMs = durationMs;
        });

        return Task.CompletedTask;
    }

    public Task LogErrorAsync(
        string action,
        Exception exception,
        int statusCode = 500,
        CancellationToken ct = default)
    {
        Enqueue(entry =>
        {
            entry.LogType = AppLogs.LogType.ActionLog.ToString();
            entry.Action = action;

            // ToString() rather than Message: an InvalidOperationException wrapping a
            // PostgresException says nothing useful without its inner exception, and the inner one
            // is where the SQLSTATE lives.
            entry.Details = Truncate(exception.ToString(), MaxDetailsLength);
            entry.ExceptionType = exception.GetType().FullName;
            entry.StackTrace = Truncate(exception.StackTrace, MaxStackTraceLength);
            entry.StatusCode = statusCode;
        });

        return Task.CompletedTask;
    }

    private void Enqueue(Action<AppLog> populate)
    {
        var current = context.Current;
        var now = clock.GetUtcNow();

        var entry = new AppLog
        {
            Source = typeof(T).Name,
            UserId = current.UserId,
            TenantId = current.TenantId,
            IpAddress = current.IpAddress,
            UserAgent = current.UserAgent,
            RequestPath = current.RequestPath,
            HttpMethod = current.HttpMethod,
            CorrelationId = current.CorrelationId,

            // Stamped here, not by ApplyAuditFields. The drain writes this row seconds later and
            // possibly out of order, so a timestamp taken at SaveChanges would record when the
            // queue got around to it rather than when the thing happened.
            CreatedAt = now,
            UpdatedAt = now,
            Action = string.Empty,
            LogType = string.Empty,
        };

        populate(entry);
        queue.TryEnqueue(entry);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
