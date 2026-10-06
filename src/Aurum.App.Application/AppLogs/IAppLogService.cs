namespace Aurum.App.Application.AppLogs;

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
/// The application's durable log. Use this, not <c>ILogger&lt;T&gt;</c>, anywhere the record needs
/// to survive the process and be queryable — controllers, handlers and the MediatR behaviors.
/// </summary>
/// <remarks>
/// Non-blocking: calls enqueue onto <see cref="IAppLogQueue"/>, so logging a 500 cannot cause a
/// second one. <c>ILogger&lt;T&gt;</c> is still right for stdout-only lines such as the poller's.
/// </remarks>
public interface IAppLogService<T>
{
    Task LogAsync(
        LogType logType,
        string action,
        string? details = null,
        int? statusCode = null,
        long? durationMs = null,
        CancellationToken ct = default);

    Task LogErrorAsync(
        string action,
        Exception exception,
        int statusCode = 500,
        CancellationToken ct = default);
}
