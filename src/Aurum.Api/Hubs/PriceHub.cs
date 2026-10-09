using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.SharedKernel.Constants;
using Microsoft.AspNetCore.SignalR;

namespace Aurum.Api.Hubs;

/// <summary>
/// Apps subscribe here, one group per symbol, and get every new price for it.
/// </summary>
/// <remarks>
/// Reads the cache and the delta engine directly, not through MediatR: a read-only query would be a
/// pass-through handler. Item 8 decides whether that changes (D-19).
/// </remarks>
public sealed class PriceHub(ILatestQuoteCache cache, IDeltaEngine deltas) : Hub<IPriceClient>
{
    public static string GroupName(string symbol) => $"price:{symbol}";

    /// <summary>Joins the symbol's group, then sends the held price and moves to the caller.</summary>
    public async Task Subscribe(string symbol)
    {
        var known = Resolve(symbol);

        // Join first. Sending first leaves a gap where a broadcast misses this caller.
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(known), Context.ConnectionAborted);
        await Clients.Caller.PriceUpdated(PriceUpdate.From(known, cache.Get(known), deltas.GetSnapshot(known)));
    }

    public Task Unsubscribe(string symbol) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(Resolve(symbol)), Context.ConnectionAborted);

    // Group names are case-sensitive, so use the stored spelling or "xauusd" becomes its own silent group.
    private static string Resolve(string symbol) =>
        SupportedSymbol.All.FirstOrDefault(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase))
        ?? throw new HubException(
            $"Unknown symbol '{symbol}'. Supported: {string.Join(", ", SupportedSymbol.All)}.");
}
