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
            // Shutdown: fall through and flush what is in hand.
        }

        // The shutdown token is already cancelled and would cancel this last write.
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
