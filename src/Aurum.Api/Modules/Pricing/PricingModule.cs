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
            // Bound onto the dictionary rather than the options object so the section's children
            // are the map's entries. Keeps the config paths operators already use
            // (PriceSources:GoldApiIo:ApiKey, PriceSources__GoldApiIo__ApiKey) while making the
            // lookup by source code a data lookup rather than a switch.
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources))
            // Covers attributes on PriceSourcesOptions itself. It does NOT reach the sources —
            // PriceSourcesOptionsValidator does that, and is what actually enforces their
            // annotations.
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();

        services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

        services.AddHttpClient<IPriceSource, GoldApiIoSource>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<PriceSourcesOptions>>()
                    .Value.RequireByCode(GoldApiIoSource.SourceCode);
                http.BaseAddress = options.BaseUrl;
                http.Timeout = options.RequestTimeout;
                http.DefaultRequestHeaders.Add("x-access-token", options.ApiKey);
            })
            .AddHttpMessageHandler(sp => new QuotaHandler(
                sp.GetRequiredService<IServiceScopeFactory>(),
                GoldApiIoSource.SourceCode,
                sp.GetRequiredService<ILogger<QuotaHandler>>()));

        services.AddHostedService<PricePollingService>();

        return services;
    }
}
