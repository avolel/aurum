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
/// It logs the failure and rethrows rather than translating: the controller's catch block owns the
/// HTTP response, and a behavior that swallowed here would hand the controller a default value it
/// would report as success.
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

        // Stopwatch, not TimeProvider: this measures elapsed duration, where a monotonic clock is
        // the correct instrument. TimeProvider is the authority for timestamps we *persist*, and a
        // wall clock that steps backwards would make a duration negative.
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
