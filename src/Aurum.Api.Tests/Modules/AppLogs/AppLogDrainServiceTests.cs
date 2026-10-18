using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.AppLogs;
using Aurum.App.Infrastructure.Data.Entities.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aurum.Api.Tests.Modules.AppLogs;

/// <summary>
/// The drain's write timing: rows reach <c>app_logs</c> while the app runs, not only at shutdown.
/// </summary>
/// <remarks>
/// Every test queues fewer rows than a full batch: at exactly a full batch, a drain that only
/// writes full batches looks correct.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AppLogDrainServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Bounds a thread-pool hop and one insert, not a batch timer.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private const string Action = "AppLogDrainServiceTests";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.AppLogs.ExecuteDeleteAsync(Ct);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private (AppLogDrainService Drain, AppLogQueue Queue) Build()
    {
        var services = new ServiceCollection();

        // Transient: the drain opens a scope per batch.
        services.AddTransient(_ => fixture.CreateDbContext());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var queue = new AppLogQueue(NullLogger<AppLogQueue>.Instance);
        var drain = new AppLogDrainService(queue, scopes, NullLogger<AppLogDrainService>.Instance);
        return (drain, queue);
    }

    private static void Enqueue(AppLogQueue queue, int count, string round)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            queue.TryEnqueue(new AppLog
            {
                LogType = LogType.ActionLog.ToString(),
                Action = Action,
                Details = round,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private async Task WaitForRowsAsync(string round, int expected)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        var seen = 0;

        while (DateTime.UtcNow < deadline)
        {
            await using var db = fixture.CreateDbContext();
            seen = await db.AppLogs.CountAsync(l => l.Action == Action && l.Details == round, Ct);
            if (seen == expected)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"Expected {expected} '{round}' rows in app_logs while running, saw {seen}.");
    }

    [Fact]
    public async Task ExecuteAsync_WithAPartialBatch_WritesItWithoutShutdown()
    {
        var (drain, queue) = Build();
        await drain.StartAsync(Ct);

        try
        {
            Enqueue(queue, 3, "first");
            await WaitForRowsAsync("first", 3);

            // A second round: a drain that writes once and then returns passes the first.
            Enqueue(queue, 3, "second");
            await WaitForRowsAsync("second", 3);
        }
        finally
        {
            await drain.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithMoreThanABatch_WritesEveryRow()
    {
        var (drain, queue) = Build();

        // Queued before start, so the drain finds them all waiting at once.
        Enqueue(queue, 250, "burst");
        await drain.StartAsync(Ct);

        try
        {
            await WaitForRowsAsync("burst", 250);
        }
        finally
        {
            await drain.StopAsync(Ct);
        }
    }
}
