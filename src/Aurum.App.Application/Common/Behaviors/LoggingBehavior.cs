using System.Diagnostics;
using Aurum.App.Logging;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Aurum.App.Application.Common.Behaviors;

/// <summary>
/// Records every MediatR request to <c>app_logs</c>, at Warning when it was slow. First in the pipeline,
/// so it measures validation and the transaction as well as the handler.
/// </summary>
/// <remarks>
/// Logs and rethrows: the controller owns the response, and swallowing would look like success.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    IAppLogService<TRequest> appLog)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>A request slower than this is logged at Warning, so a search by level finds it.</summary>
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

            // One row either way: DurationMs already says how slow, so a second row would repeat it.
            await appLog.LogAsync(
                LogType.ActionLog, action, details: null, statusCode: 200,
                durationMs: (long)elapsed.TotalMilliseconds,
                level: elapsed > SlowRequestThreshold ? LogLevel.Warning : LogLevel.Information,
                ct: cancellationToken);

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller hung up; the controller rethrows this too. Not a server fault, so no
            // 500 row. A cancellation the caller didn't ask for (a timeout) falls through below.
            throw;
        }
        catch (Exception ex)
        {
            var statusCode = ex is ValidationException ? 400 : 500;
            await appLog.LogErrorAsync(action, ex, statusCode, ct: cancellationToken);
            throw;
        }
    }
}
