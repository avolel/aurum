using System.Diagnostics;
using Aurum.App.Application.AppLogs;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Aurum.App.Application.Common.Behaviors;

/// <summary>
/// Records every MediatR request to <c>app_logs</c> and warns on slow ones. First in the pipeline,
/// so it measures validation and the transaction as well as the handler.
/// </summary>
/// <remarks>
/// Logs and rethrows: the controller owns the response, and swallowing would look like success.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    IAppLogService<TRequest> appLog,
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>A handler slower than this is a problem worth a line in stdout.</summary>
    private static readonly TimeSpan SlowRequestThreshold = TimeSpan.FromMilliseconds(500);

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var action = typeof(TRequest).Name;

        // Stopwatch, not TimeProvider: a duration needs a clock that never steps backwards.
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await next();
            var elapsed = Stopwatch.GetElapsedTime(started);

            await appLog.LogAsync(
                LogType.ActionLog, action, details: null, statusCode: 200,
                durationMs: (long)elapsed.TotalMilliseconds, ct: cancellationToken);

            if (elapsed > SlowRequestThreshold)
            {
                logger.LogWarning("{Action} took {ElapsedMs}ms.", action, elapsed.TotalMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            await appLog.LogErrorAsync(action, ex, ct: cancellationToken);
            throw;
        }
    }
}
