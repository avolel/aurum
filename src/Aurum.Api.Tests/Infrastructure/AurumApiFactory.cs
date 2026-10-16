using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// The whole API from <c>Program.cs</c>, over the collection's Postgres, with the poller removed
/// and every price source replaced by one that fails without touching the network.
/// </summary>
/// <remarks>
/// A poller inside a test makes results depend on when its timer fires, and spends real requests
/// against a 100-request month. With it gone nothing fills the cache: a test that needs a held
/// price records one into <c>ILatestQuoteCache</c> itself.
/// </remarks>
public sealed class AurumApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    public static readonly string[] SourceCodes =
        [ApiNinjasSource.SourceCode, GoldApiIoSource.SourceCode, MetalPriceApiSource.SourceCode];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, not ConfigureAppConfiguration: Program.cs reads the connection string before
        // Build(), and only host settings are visible that early.
        builder.UseSetting("ConnectionStrings:DefaultConnection", postgres.ConnectionString);

        // The shipped file leaves every key blank, which fails boot (D-9). Keyed by config key, not code.
        foreach (var configKey in new[] { "GoldApiIo", "ApiNinjas", "MetalpriceApi" })
        {
            builder.UseSetting($"PriceSources:{configKey}:ApiKey", "test-key");
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPriceSource>();
            foreach (var code in SourceCodes)
            {
                services.AddSingleton<IPriceSource>(ScriptedPriceSource.Faulting(code, "no network in tests"));
            }

            var poller = services.Single(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PricePollingService));
            services.Remove(poller);
        });
    }
}
