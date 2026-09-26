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
/// <para>
/// Non-blocking: every call enqueues onto <see cref="IAppLogQueue"/> and returns. The request
/// thread never waits on a database write, which is the point — logging a 500 must not be able to
/// turn into a second 500.
/// </para>
/// <para>
/// <c>ILogger&lt;T&gt;</c> is still correct for anything whose audience is an operator reading
/// stdout: the poller's tick lines, the startup budget coverage, the governor's denial message.
/// The distinction is durability and queryability, not importance.
/// </para>
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
