using Aurum.App.Infrastructure.Pricing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// The pricing options registered the way <c>Program.cs</c> registers them, so a test resolving
/// <c>IOptions&lt;PriceSourcesOptions&gt;</c> runs the same validation the host runs at boot.
/// </summary>
internal static class PricingOptionsHost
{
    /// <param name="registeredCodes">Stand-ins for <c>Program.AddPriceSource</c>'s registrations.</param>
    public static ServiceProvider Build(IConfiguration config, params string[] registeredCodes)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);

        services.AddOptions<PriceSourcesOptions>()
            .Configure<IConfiguration>((options, cfg) =>
                cfg.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources))
            .ValidateDataAnnotations();

        services.AddOptions<PricePollingOptions>()
            .BindConfiguration(PricePollingOptions.SectionName);

        services.AddOptions<PriceFeedResilienceOptions>()
            .BindConfiguration(PriceFeedResilienceOptions.SectionName)
            .ValidateDataAnnotations();

        services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();

        foreach (var code in registeredCodes)
        {
            services.AddSingleton(new RegisteredPriceSource(code));
        }

        return services.BuildServiceProvider();
    }
}
