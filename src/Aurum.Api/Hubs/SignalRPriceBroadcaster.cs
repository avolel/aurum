using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Aurum.Api.Hubs;

/// <summary>Sends to the symbol's <see cref="PriceHub"/> group from outside the hub.</summary>
internal sealed class SignalRPriceBroadcaster(
    IHubContext<PriceHub, IPriceClient> hub,
    ILatestQuoteCache cache,
    IDeltaEngine deltas) : IPriceBroadcaster
{
    // Typed client methods take no token; a group send only queues into each connection's buffer.
    public Task PublishAsync(string symbol, CancellationToken ct) =>
        hub.Clients.Group(PriceHub.GroupName(symbol))
            .PriceUpdated(PriceUpdate.From(symbol, cache.Get(symbol), deltas.GetSnapshot(symbol)));
}
