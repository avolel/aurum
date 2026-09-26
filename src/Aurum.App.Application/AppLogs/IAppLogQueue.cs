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
/// <para>
/// A bounded channel with <see cref="BoundedChannelFullMode.DropWrite"/>. Bounded because the
/// failure this guards is a database that has gone away: an unbounded queue turns "logging is
/// degraded" into an out-of-memory kill of a process that was otherwise serving traffic.
/// </para>
/// <para>
/// Dropping the newest write rather than the oldest is deliberate. When the drain has stalled, the
/// rows already queued are the ones nearest the cause; the flood arriving behind them is the
/// symptom. Each drop is counted and reported to <c>ILogger</c>, because a silent drop would make
/// the log lie by omission at exactly the moment someone is reading it.
/// </para>
/// </remarks>
public sealed class AppLogQueue(ILogger<AppLogQueue> logger) : IAppLogQueue
{
    /// <summary>
    /// Roughly a minute of a busy endpoint. Large enough to absorb a database blip, small enough
    /// that a sustained outage is bounded memory rather than a leak.
    /// </summary>
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

        // Powers of ten rather than every drop: the condition that causes one drop causes
        // thousands, and a log line per drop is a second flood on top of the first.
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
