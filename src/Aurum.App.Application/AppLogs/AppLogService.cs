using Aurum.App.Infrastructure.Data.Entities.Logging;

namespace Aurum.App.Application.AppLogs;

/// <inheritdoc />
public sealed class AppLogService<T>(
    IAppLogQueue queue,
    IRequestContextAccessor context,
    TimeProvider clock) : IAppLogService<T>
{
    /// <summary>
    /// Matches <c>app_logs.details</c>. Cut here, or one long row fails its whole batch.
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

            // ToString(), not Message, to keep inner exceptions (where the SQLSTATE lives).
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

            // Stamped now, not at save: the drain writes it later.
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
