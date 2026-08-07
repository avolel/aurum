using Aurum.Api.Modules.Pricing.Jobs;
using Aurum.Api.Modules.Pricing.Quota;
using Aurum.Api.Modules.Pricing.Sources;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Modules.Pricing;

public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<PriceSourcesOptions>()
            .Bind(config.GetSection(PriceSourcesOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

        services.AddHttpClient<IPriceSource, GoldApiIoSource>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<PriceSourcesOptions>>().Value.GoldApiIo;
                http.BaseAddress = options.BaseUrl;
                http.Timeout = options.RequestTimeout;
                http.DefaultRequestHeaders.Add("x-access-token", options.ApiKey);
            })
            .AddHttpMessageHandler(sp => new QuotaHandler(
                sp.GetRequiredService<IServiceScopeFactory>(),
                GoldApiIoOptions.SourceCode,
                sp.GetRequiredService<ILogger<QuotaHandler>>()));

        services.AddHostedService<PricePollingService>();

        return services;
    }
}
