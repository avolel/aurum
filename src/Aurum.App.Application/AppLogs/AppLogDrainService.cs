using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aurum.App.Application.AppLogs;

/// <summary>
/// Drains <see cref="IAppLogQueue"/> into <c>app_logs</c> in batches.
/// </summary>
/// <remarks>
/// <para>
/// A scope per batch, not per row and not one for the process lifetime. Per row is a DbContext
/// construction per log line; process-lifetime is a DbContext that accumulates tracked entities
/// forever and never sees a schema change.
/// </para>
/// <para>
/// A failed batch is dropped rather than retried. Retrying a batch that failed because the
/// database is unreachable produces an infinite loop that holds the queue at capacity, so the
/// log's own failure mode would start dropping the newest rows — the ones about the outage.
/// The loss is reported on <c>ILogger</c>, which goes to stdout and does not need the database.
/// </para>
/// </remarks>
public sealed class AppLogDrainService(
    IAppLogQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AppLogDrainService> logger) : BackgroundService
{
    private const int MaxBatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AppLog>(MaxBatchSize);

        try
        {
            await foreach (var entry in queue.ReadAllAsync(stoppingToken))
            {
                batch.Add(entry);

                if (batch.Count >= MaxBatchSize)
                {
                    await FlushAsync(batch, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Fall through and make one last attempt at whatever is in hand.
        }

        // CancellationToken.None: the host's shutdown token is already cancelled by the time we
        // get here, and passing it would cancel the write that exists to save these rows.
        await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<AppLog> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();
            db.AppLogs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist {Count} application log entries; they are lost.",
                batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
