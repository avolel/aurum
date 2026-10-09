namespace Aurum.App.Infrastructure.Pricing.Realtime;

/// <summary>
/// Sends a symbol's held price and moves to connected apps. Keeps SignalR out of this project (D-19).
/// </summary>
public interface IPriceBroadcaster
{
    /// <summary>
    /// Reads the symbol's current state from the cache and the delta engine and sends it to that
    /// symbol's subscribers.
    /// </summary>
    Task PublishAsync(string symbol, CancellationToken ct);
}
