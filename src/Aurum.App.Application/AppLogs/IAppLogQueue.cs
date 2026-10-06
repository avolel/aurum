using System.Threading.Channels;
using Aurum.App.Infrastructure.Data.Entities.Logging;
using Microsoft.Extensions.Logging;

namespace Aurum.App.Application.AppLogs;

/// <summary>Hand-off between the request thread and the drain.</summary>
public interface IAppLogQueue
{
    /// <summary>
    /// Enqueues a row. Never blocks and never throws: a full queue drops the row and says so on
    /// <c>ILogger</c>.
    /// </summary>
    /// <returns><see langword="false"/> when the row was dropped.</returns>
    bool TryEnqueue(AppLog entry);

    IAsyncEnumerable<AppLog> ReadAllAsync(CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// Bounded, so a dead database cannot run the process out of memory. Drops the newest write: the
/// rows already queued are nearest the cause. Drops are counted and reported, never silent.
/// </remarks>
public sealed class AppLogQueue(ILogger<AppLogQueue> logger) : IAppLogQueue
{
    /// <summary>Roughly a minute of a busy endpoint: enough to ride out a database blip.</summary>
    private const int Capacity = 10_000;

    private readonly Channel<AppLog> _channel = Channel.CreateBounded<AppLog>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private long _dropped;

    public bool TryEnqueue(AppLog entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        var dropped = Interlocked.Increment(ref _dropped);

        // Log at powers of ten, not every drop, or the warnings become a flood too.
        if (IsPowerOfTen(dropped))
        {
            logger.LogWarning(
                "AppLog queue is full; dropped {Dropped} entries so far. Most recent: {Action}.",
                dropped, entry.Action);
        }

        return false;
    }

    public IAsyncEnumerable<AppLog> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    private static bool IsPowerOfTen(long value)
    {
        while (value >= 10 && value % 10 == 0)
        {
            value /= 10;
        }

        return value == 1;
    }
}
