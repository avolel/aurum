namespace Aurum.Api.Hubs;

/// <summary>
/// What the server can call on a connected app. The method name is the SignalR event name.
/// </summary>
public interface IPriceClient
{
    Task PriceUpdated(PriceUpdate update);
}
