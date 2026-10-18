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
/// <para>A scope per batch: per row is wasteful, and one for the process tracks entities forever.</para>
/// <para>A failed batch is dropped, not retried: retrying against a dead database would fill the
/// queue and drop the rows about the outage. The loss goes to <c>ILogger</c> (stdout).</para>
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
            while (await queue.WaitToReadAsync(stoppingToken))
            {
                await DrainAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: fall through and write what is still queued.
        }

        // The shutdown token is already cancelled and would cancel these last writes.
        await DrainAsync(batch, CancellationToken.None);
    }

    /// <summary>
    /// Writes everything queued right now, in batches. The last batch is written even when short:
    /// waiting for a full one would hold a quiet API's rows in memory indefinitely.
    /// </summary>
    private async Task DrainAsync(List<AppLog> batch, CancellationToken ct)
    {
        while (queue.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= MaxBatchSize)
            {
                await FlushAsync(batch, ct);
            }
        }

        await FlushAsync(batch, ct);
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
