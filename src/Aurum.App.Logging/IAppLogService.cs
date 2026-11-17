using Microsoft.Extensions.Logging;

namespace Aurum.App.Logging;

public enum LogType
{
    /// <summary>A controller action or handler completing, success or failure.</summary>
    ActionLog,

    /// <summary>A background job or scheduled task.</summary>
    SystemLog,

    /// <summary>Authentication, authorization and configuration-surface events.</summary>
    SecurityLog,
}

/// <summary>
/// The application's durable log, saved to <c>app_logs</c> and never the console. Use this, not
/// <c>ILogger&lt;T&gt;</c>, everywhere: controllers, handlers, behaviors and background jobs (D-25).
/// </summary>
/// <remarks>
/// Non-blocking: calls enqueue onto <see cref="IAppLogQueue"/>, so logging a 500 cannot cause a
/// second one. Only the queue and its drain keep <c>ILogger&lt;T&gt;</c>: the log can't report its
/// own failure through itself.
/// </remarks>
public interface IAppLogService<T>
{
    Task LogAsync(
        LogType logType,
        string action,
        string? details = null,
        int? statusCode = null,
        long? durationMs = null,
        LogLevel level = LogLevel.Information,
        CancellationToken ct = default);

    Task LogErrorAsync(
        string action,
        Exception exception,
        int statusCode = 500,
        string? details = null,
        LogType logType = LogType.ActionLog,
        CancellationToken ct = default);
}
