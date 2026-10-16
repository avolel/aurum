using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Sources;
using Aurum.App.SharedKernel.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// The test host boots from the real <c>Program.cs</c> and cannot reach a price service.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AurumApiFactoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AurumApiFactory _factory = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void The_test_host_has_no_poller_and_no_real_price_source()
    {
        var services = _factory.Services;

        Assert.DoesNotContain(services.GetServices<IHostedService>(), s => s is PricePollingService);
        Assert.All(services.GetServices<IPriceSource>(), s => Assert.IsType<ScriptedPriceSource>(s));
        Assert.Equal(AurumApiFactory.SourceCodes.Order(), services.GetServices<IPriceSource>().Select(s => s.Code).Order());
    }

    [Fact]
    public async Task Live_price_is_503_until_a_quote_is_held_then_serves_it()
    {
        var client = _factory.CreateClient();

        var before = await client.GetAsync("/api/v1/price/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        var at = DateTimeOffset.UtcNow;
        var quote = new PriceQuote(SupportedSymbol.Gold, at, at, null, null, 4_321.5m, ApiNinjasSource.SourceCode);
        _factory.Services.GetRequiredService<ILatestQuoteCache>()
            .Record(new PriceFeedResult(quote, ApiNinjasSource.SourceCode, []));

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/price/live");
        Assert.Equal(4_321.5m, body.GetProperty("data").GetProperty("mid").GetDecimal());
    }

    [Fact]
    public async Task Sources_lists_every_configured_source_in_priority_order()
    {
        var body = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/price/sources");

        var codes = body.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("sourceCode").GetString());
        Assert.Equal(AurumApiFactory.SourceCodes, codes);
    }
}
