using Aurum.App.Infrastructure.Data.Entities.Logging;
using Aurum.App.SharedKernel.Constants;
using System.Diagnostics.CodeAnalysis;

namespace Aurum.App.Application.AppLogs;

/// <inheritdoc />
public sealed class AppLogService<T>(
    IAppLogQueue queue,
    IRequestContextAccessor context,
    TimeProvider clock) : IAppLogService<T>
{
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
            entry.Details = Truncate(details, AppLogColumnLengths.MaxDetailsLength);
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
            entry.Details = Truncate(exception.ToString(), AppLogColumnLengths.MaxDetailsLength);
            entry.ExceptionType = Truncate(exception.GetType().FullName, AppLogColumnLengths.MaxExceptionTypeLength);
            entry.StackTrace = Truncate(exception.StackTrace, AppLogColumnLengths.MaxStackTraceLength);
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
            UserId = Truncate(current.UserId, AppLogColumnLengths.MaxUserIdLength),
            TenantId = Truncate(current.TenantId, AppLogColumnLengths.MaxTenantIdLength),
            IpAddress = Truncate(current.IpAddress, AppLogColumnLengths.MaxIpAddressLength),
            UserAgent = Truncate(current.UserAgent, AppLogColumnLengths.MaxUserAgentLength),
            RequestPath = Truncate(current.RequestPath, AppLogColumnLengths.MaxRequestPathLength),
            HttpMethod = Truncate(current.HttpMethod, AppLogColumnLengths.MaxHttpMethodLength),
            CorrelationId = Truncate(current.CorrelationId, AppLogColumnLengths.MaxCorrelationIdLength),

            // Stamped now, not at save: the drain writes it later.
            CreatedAt = now,
            UpdatedAt = now,
            Action = string.Empty,
            LogType = string.Empty,
        };

        populate(entry);
        entry.Action = Truncate(entry.Action, AppLogColumnLengths.MaxActionLength);
        queue.TryEnqueue(entry);
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
