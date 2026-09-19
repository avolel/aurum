using Aurum.Api.Modules.Pricing.Jobs;
using Aurum.Api.Modules.Pricing.Quota;
using Aurum.Api.Modules.Pricing.Sources;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

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
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PricePollingOptions>()
            .BindConfiguration(PricePollingOptions.SectionName)
            .Validate(
                o => o.PollInterval > TimeSpan.Zero,
                $"{PricePollingOptions.SectionName}:PollInterval must be set to a positive interval.")
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();

        services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

        // Register each source's typed client with its QuotaHandler, then project it onto IPriceSource
        services.AddPriceSource<GoldApiIoSource>(GoldApiIoSource.SourceCode, (builder, getOptions) =>
            builder.ConfigureHttpClient((sp, http) =>
                http.DefaultRequestHeaders.Add("x-access-token", getOptions(sp).ApiKey)));

        services.AddPriceSource<ApiNinjasSource>(ApiNinjasSource.SourceCode, (builder, getOptions) =>
            builder.ConfigureHttpClient((sp, http) =>
                http.DefaultRequestHeaders.Add("X-Api-Key", getOptions(sp).ApiKey)));

        services.AddPriceSource<MetalPriceApiSource>(MetalPriceApiSource.SourceCode, (builder, getOptions) =>
            builder.AddHttpMessageHandler(sp => new QueryKeyAuthHandler(getOptions(sp).ApiKey)));

        services.AddHostedService<PricePollingService>();

        return services;
    }

    /// <summary>
    /// Registers a source's typed client with its QuotaHandler, then projects it onto IPriceSource
    /// so the failover chain can resolve IEnumerable&lt;IPriceSource&gt;.
    /// </summary>
    /// <remarks>
    /// The handler is attached here rather than at each call site because a source registered without
    /// it still compiles, still works, and spends its budget uncounted — the one wiring mistake that
    /// produces no symptom until the provider starts rejecting requests.
    ///
    /// <paramref name="configureAuth"/> exists because auth is the only genuinely per-provider part:
    /// GoldAPI uses x-access-token, API Ninjas X-Api-Key, and MetalpriceAPI a query parameter.
    /// </remarks>
    private static IServiceCollection AddPriceSource<T>(
        this IServiceCollection services,
        string sourceCode,
        Action<IHttpClientBuilder, Func<IServiceProvider, PriceSourceOptions>> configureAuth)
        where T : class, IPriceSource
    {
        PriceSourceOptions GetOptions(IServiceProvider sp) =>
            sp.GetRequiredService<IOptions<PriceSourcesOptions>>().Value.RequireByCode(sourceCode);

        var builder = services.AddHttpClient<T>((sp, http) =>
            {
                var options = GetOptions(sp);
                http.BaseAddress = options.BaseUrl;
                // Infinite here so the ONLY cancellation comes from inside the pipeline, where the
                // retry predicate can observe it. The real ceiling is the total-timeout strategy below.
                http.Timeout = Timeout.InfiniteTimeSpan;
            });

        // Registered before QuotaHandler so the retry loop sits *above* the governor: each attempt
        // passes through the handler and is charged its own lease. Inverting the two would retry
        // below the accounting and spend the budget uncounted (D-7).
        //
        // Split out of the fluent chain because AddResilienceHandler returns
        // IHttpResiliencePipelineBuilder, not IHttpClientBuilder — nothing can be chained after it.
        builder.AddResilienceHandler("price-source", (pipeline, context) =>
            {
                var options = GetOptions(context.ServiceProvider);

                // Outermost: the whole retry sequence can't run away forever.
                pipeline.AddTimeout(options.TotalTimeout);

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    // TimeoutRejectedException is what AddTimeout throws — a real, typed exception
                    // the predicate can see, unlike the TaskCanceledException http.Timeout produced.
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<TimeoutRejectedException>()
                        .Handle<HttpRequestException>(),
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,
                });

                // Innermost: a fresh budget for EACH attempt, because it's inside the retry loop.
                pipeline.AddTimeout(options.RequestTimeout);
            });

        builder.AddHttpMessageHandler(sp => new QuotaHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sourceCode,
            sp.GetRequiredService<ILogger<QuotaHandler>>()));

        configureAuth(builder, GetOptions);

        // Transient, matching the typed client's own lifetime. Resolving IEnumerable<IPriceSource>
        // then yields every registered source; the chain orders them by Priority.
        services.AddTransient<IPriceSource>(sp => sp.GetRequiredService<T>());
        services.AddSingleton(new RegisteredPriceSource(sourceCode));

        return services;
    }
}
