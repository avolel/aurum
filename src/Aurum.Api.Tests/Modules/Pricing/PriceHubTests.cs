using System.Text.Json;
using System.Threading.Channels;
using Aurum.Api.Hubs;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using Aurum.App.Infrastructure.Pricing.Realtime;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The hub and the sender over a real SignalR connection, in memory. No database, no Program.cs.
/// </summary>
/// <remarks>
/// Over the wire because the risks are there: JSON naming, nulls inside a dictionary, group names.
/// Messages are read as raw JSON so a test checks what the app receives, not the record.
/// </remarks>
public sealed class PriceHubTests : IAsyncLifetime
{
    private const string Primary = GoldApiIoSource.SourceCode;
    private const string Route = "/hubs/price";

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromMilliseconds(250);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<HubConnection> _connections = [];
    private LatestQuoteCache _cache = null!;
    private DeltaEngine _deltas = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        // Warm-up is the only path that opens a scope, and nothing here warms.
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var polling = Options.Create(new PricePollingOptions { PollInterval = TimeSpan.FromMinutes(15) });

        _cache = new LatestQuoteCache(
            scopes, TestPriceSources.ForChain((Primary, 1, true)), _clock, polling,
            NullLogger<LatestQuoteCache>.Instance);
        _deltas = new DeltaEngine(
            scopes, Options.Create(new DeltaEngineOptions()), _clock, NullLogger<DeltaEngine>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();

        // Instances, and the fake clock is not registered: SignalR's own timers stay on real time.
        builder.Services.AddSingleton<ILatestQuoteCache>(_cache);
        builder.Services.AddSingleton<IDeltaEngine>(_deltas);
        builder.Services.AddSingleton<IPriceBroadcaster, SignalRPriceBroadcaster>();

        _app = builder.Build();
        _app.MapHub<PriceHub>(Route);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _app.DisposeAsync();
    }

    private IPriceBroadcaster Broadcaster => _app.Services.GetRequiredService<IPriceBroadcaster>();

    /// <summary>Connects a client and returns the channel its <c>PriceUpdated</c> messages land in.</summary>
    private async Task<(HubConnection Connection, ChannelReader<JsonElement> Messages)> ConnectAsync()
    {
        var server = _app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, Route), options =>
            {
                // TestServer has no socket; long polling runs over its in-memory HttpClient handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        _connections.Add(connection);

        var messages = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(nameof(IPriceClient.PriceUpdated), message => messages.Writer.TryWrite(message));

        await connection.StartAsync();
        return (connection, messages.Reader);
    }

    private static async Task<JsonElement> NextAsync(ChannelReader<JsonElement> messages) =>
        await messages.ReadAsync().AsTask().WaitAsync(MessageTimeout);

    private void Hold(DateTimeOffset observedAt, decimal mid, string symbol = SupportedSymbol.Gold)
    {
        var quote = new PriceQuote(symbol, observedAt, observedAt, null, null, mid, Primary);
        _cache.Record(new PriceFeedResult(quote, Primary, [new SourceAttempt(Primary, SourceAttemptOutcome.Success)]));
        _deltas.Record(quote);
    }

    /// <summary>
    /// Two prices 15 minutes apart: only the 15m window has an answer. Every shorter window's start
    /// is too far from its ideal and every longer one has no start at all.
    /// </summary>
    private void HoldFifteenMinuteMove()
    {
        Hold(Start.AddMinutes(-15), 4_000m);
        Hold(Start, 4_010m);
    }

    [Fact]
    public async Task Subscribe_sends_the_held_price_and_moves_immediately()
    {
        HoldFifteenMinuteMove();
        var (connection, messages) = await ConnectAsync();

        await connection.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Gold);
        var update = await NextAsync(messages);

        Assert.Equal(SupportedSymbol.Gold, update.GetProperty("symbol").GetString());

        var quote = update.GetProperty("quote");
        Assert.Equal(4_010m, quote.GetProperty("mid").GetDecimal());
        Assert.Equal(Primary, quote.GetProperty("sourceCode").GetString());
        Assert.False(quote.GetProperty("isStale").GetBoolean());

        var fifteen = update.GetProperty("windows").GetProperty(DeltaWindow.FifteenMinutes.Code);
        Assert.Equal(0.25m, fifteen.GetProperty("deltaPercent").GetDecimal());
        Assert.Equal(10m, fifteen.GetProperty("deltaAbsolute").GetDecimal());
    }

    /// <summary>D-17 on the wire: the key is present and its value is JSON null, not a zero object.</summary>
    [Fact]
    public async Task A_window_with_no_answer_arrives_as_null_not_zero()
    {
        HoldFifteenMinuteMove();
        var (connection, messages) = await ConnectAsync();

        await connection.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Gold);
        var windows = (await NextAsync(messages)).GetProperty("windows");

        Assert.Equal(
            DeltaWindow.All.Select(w => w.Code),
            windows.EnumerateObject().Select(p => p.Name));

        foreach (var window in DeltaWindow.All.Where(w => w != DeltaWindow.FifteenMinutes))
        {
            Assert.Equal(JsonValueKind.Null, windows.GetProperty(window.Code).ValueKind);
        }
    }

    [Fact]
    public async Task Subscribe_with_no_price_sends_a_null_quote()
    {
        var (connection, messages) = await ConnectAsync();

        await connection.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Gold);
        var update = await NextAsync(messages);

        Assert.Equal(JsonValueKind.Null, update.GetProperty("quote").ValueKind);
        Assert.All(update.GetProperty("windows").EnumerateObject(),
            window => Assert.Equal(JsonValueKind.Null, window.Value.ValueKind));
    }

    [Fact]
    public async Task Symbol_case_does_not_split_groups()
    {
        HoldFifteenMinuteMove();
        var (connection, messages) = await ConnectAsync();

        await connection.InvokeAsync(nameof(PriceHub.Subscribe), "xauusd");
        var snapshot = await NextAsync(messages);
        Assert.Equal(SupportedSymbol.Gold, snapshot.GetProperty("symbol").GetString());

        await Broadcaster.PublishAsync(SupportedSymbol.Gold, CancellationToken.None);

        var broadcast = await NextAsync(messages);
        Assert.Equal(SupportedSymbol.Gold, broadcast.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task Unknown_symbol_is_refused()
    {
        var (connection, _) = await ConnectAsync();

        var refused = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(nameof(PriceHub.Subscribe), "XAUEUR"));

        Assert.Contains("XAUEUR", refused.Message);
        Assert.Contains(SupportedSymbol.Gold, refused.Message);
    }

    [Fact]
    public async Task Publish_reaches_only_that_symbols_group()
    {
        var (gold, goldMessages) = await ConnectAsync();
        var (silver, silverMessages) = await ConnectAsync();

        await gold.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Gold);
        await silver.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Silver);
        await NextAsync(goldMessages);
        await NextAsync(silverMessages);

        await Broadcaster.PublishAsync(SupportedSymbol.Gold, CancellationToken.None);

        Assert.Equal(SupportedSymbol.Gold, (await NextAsync(goldMessages)).GetProperty("symbol").GetString());

        // Long polling delivers on the next poll, so give a stray message time to show up.
        await Task.Delay(QuietTimeout);
        Assert.False(silverMessages.TryRead(out _));
    }

    [Fact]
    public async Task Unsubscribe_stops_broadcasts()
    {
        var (connection, messages) = await ConnectAsync();

        await connection.InvokeAsync(nameof(PriceHub.Subscribe), SupportedSymbol.Gold);
        await NextAsync(messages);
        await connection.InvokeAsync(nameof(PriceHub.Unsubscribe), SupportedSymbol.Gold);

        await Broadcaster.PublishAsync(SupportedSymbol.Gold, CancellationToken.None);

        await Task.Delay(QuietTimeout);
        Assert.False(messages.TryRead(out _));
    }
}
