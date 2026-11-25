using System.Net;
using Aurum.Api;
using Aurum.Api.Hubs;
using Aurum.App.Logging;
using Aurum.App.Application.Common.Behaviors;
using Aurum.App.Infrastructure.AI.Ollama;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Repositories;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using FluentValidation;
using MediatR;
using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Macro;
using Aurum.App.Infrastructure.Pricing.News;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Realtime;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;
using Serilog;

// Every service registration in the application lives in this file (D-5).

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddOpenApi();
builder.Services.AddControllers();

// The only clock for every timestamp the app writes (D-7).
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
}

builder.Services.AddDbContext<AurumDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// ── CQRS ──────────────────────────────────────────────────────────────────────────────────────

// Anchored on a type in Aurum.App.Application; the executing assembly here is Aurum.Api.
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<LoggingBehavior<IRequest<Unit>, Unit>>());

builder.Services.AddValidatorsFromAssemblyContaining<ValidationBehavior<IRequest<Unit>, Unit>>();

// Registration order is pipeline order. Logging outermost so a ValidationException is logged;
// Transaction innermost so a validation failure never opens a transaction.
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

// ── Application logging ───────────────────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestContextAccessor, HttpRequestContextAccessor>();

// Singleton because it owns the channel; a scoped queue would drop its rows when the scope ends.
builder.Services.AddSingleton<IAppLogQueue, AppLogQueue>();
builder.Services.AddSingleton(typeof(IAppLogService<>), typeof(AppLogService<>));
builder.Services.AddHostedService<AppLogDrainService>();

// ── Pricing options ───────────────────────────────────────────────────────────────────────────

builder.Services.AddOptions<PriceSourcesOptions>()
    // Bound onto the dictionary so the section's children are the map's entries (D-9).
    .Configure<IConfiguration>((options, cfg) =>
        cfg.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PricePollingOptions>()
    .BindConfiguration(PricePollingOptions.SectionName)
    .Validate(
        o => o.PollInterval > TimeSpan.Zero,
        $"{PricePollingOptions.SectionName}:PollInterval must be set to a positive interval.")
    .Validate(
        PricePollingOptions.StaleAfterExceedsPollInterval,
        $"{PricePollingOptions.SectionName}:StaleAfter must be unset or longer than PollInterval; "
          + "at or below it, every price is flagged stale one poll after it arrives.")
    .ValidateOnStart();

// Reads PollInterval rather than duplicating it: the buffer must cover 1d at the real cadence (D-17).
builder.Services.AddOptions<DeltaEngineOptions>()
    .BindConfiguration(DeltaEngineOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate<IOptions<PricePollingOptions>>(
        (o, polling) => DeltaEngineOptions.BufferCoversLongestWindow(o, polling.Value.PollInterval),
        $"{DeltaEngineOptions.SectionName}:MaxSamplesPerSymbol must hold one day plus the 1d window's "
          + "tolerance at PricePolling:PollInterval; below that the 1d window never has a starting price.")
    .ValidateOnStart();

builder.Services.AddOptions<SignificanceOptions>()
    .BindConfiguration(SignificanceOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        SignificanceOptions.WindowKeysAreKnown,
        $"{SignificanceOptions.SectionName}:Windows has a key that is not a window code "
          + $"({string.Join(", ", DeltaWindow.All.Select(w => w.Code))}); a typo switches that window off.")
    .Validate(
        SignificanceOptions.ValuesAreInRange,
        $"{SignificanceOptions.SectionName}: every MinPercent and Cooldown must be positive, and "
          + "CrossSourceMagnitudeMultiplier must be at least 1.")
    .ValidateOnStart();

builder.Services.AddOptions<PriceFeedResilienceOptions>()
    .BindConfiguration(PriceFeedResilienceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PriceFeedCircuitOptions>()
    .BindConfiguration(PriceFeedCircuitOptions.SectionName)
    .ValidateOnStart();

// ValidateDataAnnotations() does not descend into the source map; this validator does (D-9).
builder.Services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PriceFeedCircuitOptions>, PriceFeedCircuitOptionsValidator>();

// ── Pricing services ──────────────────────────────────────────────────────────────────────────

// Singletons: each holds state that must outlive a poll, and none holds an HttpClient (D-12).
// The cache and the delta engine reach the database through IServiceScopeFactory (D-16, D-17).
// A factory because the constructor is internal, and the container only calls public ones (D-20).
builder.Services.AddSingleton(sp => new SourceCircuitStore(
    sp.GetServices<RegisteredPriceSource>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IOptions<PriceFeedCircuitOptions>>()));
builder.Services.AddSingleton<ILatestQuoteCache, LatestQuoteCache>();
builder.Services.AddSingleton<IDeltaEngine, DeltaEngine>();

builder.Services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

// Scoped: it holds typed clients, whose handlers the factory recycles (D-12).
builder.Services.AddScoped<IPriceFeed, FailoverPriceFeed>();

// Each source's typed client, its resilience pipeline and its QuotaHandler.
Program.AddPriceSource<GoldApiIoSource>(builder.Services, GoldApiIoSource.SourceCode, (b, getOptions) =>
    b.ConfigureHttpClient((sp, http) =>
        http.DefaultRequestHeaders.Add("x-access-token", getOptions(sp).ApiKey)));

Program.AddPriceSource<ApiNinjasSource>(builder.Services, ApiNinjasSource.SourceCode, (b, getOptions) =>
    b.ConfigureHttpClient((sp, http) =>
        http.DefaultRequestHeaders.Add("X-Api-Key", getOptions(sp).ApiKey)));

Program.AddPriceSource<MetalPriceApiSource>(builder.Services, MetalPriceApiSource.SourceCode, (b, getOptions) =>
    b.AddHttpMessageHandler(sp => new QueryKeyAuthHandler(getOptions(sp).ApiKey)));

builder.Services.AddHostedService<PricePollingService>();

// ── Macro data ────────────────────────────────────────────────────────────────────────────────

// A blank key fails boot rather than every request (D-9, D-22).
builder.Services.AddOptions<FredOptions>()
    .BindConfiguration(FredOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MacroPollingOptions>()
    .BindConfiguration(MacroPollingOptions.SectionName)
    .Validate(
        o => o.RefreshInterval > TimeSpan.Zero,
        $"{MacroPollingOptions.SectionName}:RefreshInterval must be set to a positive interval.")
    .Validate<TimeProvider>(
        (o, clock) => o.HistoryStart < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
        $"{MacroPollingOptions.SectionName}:HistoryStart must be set to a past date.")
    .ValidateOnStart();

// Standard retries only, no QuotaHandler: FRED limits per minute, not per month (D-22).
var fredClient = builder.Services.AddHttpClient<FredClient>((sp, http) =>
    http.BaseAddress = sp.GetRequiredService<IOptions<FredOptions>>().Value.BaseUrl);

// Not chained: AddStandardResilienceHandler does not return IHttpClientBuilder.
fredClient.AddStandardResilienceHandler();
fredClient.AddHttpMessageHandler(sp =>
    new QueryKeyAuthHandler(sp.GetRequiredService<IOptions<FredOptions>>().Value.ApiKey));

// Release-morning figures (D-23). The key goes in the POST body, so no auth handler. No QuotaHandler:
// BLS limits per day, and a retried 429 costs at most 3 of the 500.
builder.Services.AddOptions<BlsOptions>()
    .BindConfiguration(BlsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<BlsClient>((sp, http) =>
        http.BaseAddress = sp.GetRequiredService<IOptions<BlsOptions>>().Value.BaseUrl)
    .AddStandardResilienceHandler();

// Scoped: it holds the request's AurumDbContext.
builder.Services.AddScoped<FredImporter>();

// The only macro writer (D-23).
builder.Services.AddHostedService<MacroPollingService>();

// ── News ──────────────────────────────────────────────────────────────────────────────────────

builder.Services.AddOptions<NewsPollingOptions>()
    .BindConfiguration(NewsPollingOptions.SectionName)
    .Validate(
        o => o.Interval > TimeSpan.Zero,
        $"{NewsPollingOptions.SectionName}:Interval must be set to a positive interval.")
    .ValidateOnStart();

// Standard retries only, no QuotaHandler: the feeds have no allowance. The feed's absolute URL is
// the request, so no BaseAddress.
builder.Services.AddHttpClient<RssClient>().AddStandardResilienceHandler();

// Scoped: it holds the request's AurumDbContext.
builder.Services.AddScoped<NewsImporter>();

// The only news writer, so NewsImporter's load-then-insert is safe.
builder.Services.AddHostedService<NewsPollingService>();

// ── AI (Ollama) ───────────────────────────────────────────────────────────────────────────────

builder.Services.AddOptions<OllamaOptions>()
    .BindConfiguration(OllamaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ValidateDataAnnotations() does not descend into the job map; this validator does.
builder.Services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();

// Named, not typed: the container makes a new typed client each time, and each would get its own lock.
builder.Services.AddHttpClient(OllamaOptions.HttpClientName, (sp, http) =>
    {
        http.BaseAddress = sp.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl;
        // Off, so the 100 s default can't cut in before the pipeline's own limits.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .AddStandardResilienceHandler()
    .Configure((resilience, sp) =>
    {
        var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;

        resilience.AttemptTimeout.Timeout = ollama.RequestTimeout;
        resilience.TotalRequestTimeout.Timeout = ollama.TotalTimeout;

        // Polly counts retries, the setting counts attempts.
        resilience.Retry.MaxRetryAttempts = ollama.MaxAttempts - 1;
        // Fixed and without jitter, so TotalTimeout's sum matches the real schedule.
        resilience.Retry.BackoffType = DelayBackoffType.Constant;
        resilience.Retry.Delay = OllamaOptions.RetryDelay;
        resilience.Retry.UseJitter = false;

        // The handler refuses to start if this is under twice the attempt timeout.
        resilience.CircuitBreaker.SamplingDuration = ollama.RequestTimeout * 2;
    });

// ── Health ────────────────────────────────────────────────────────────────────────────────────

// /health is liveness; /health/ready checks dependencies, selected by the "ready" tag.
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

// ── SignalR ───────────────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPriceBroadcaster, SignalRPriceBroadcaster>();

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Aurum API v1"));
}

app.MapHub<PriceHub>("/hubs/price");
app.MapControllers();
app.MapHealthChecks("/health", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

await MigrateAsync(app);

app.Run();

// Single-instance only: concurrent migrations race. Becomes a separate step with Phase 5's replicas.
static async Task MigrateAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();
    await db.Database.MigrateAsync();
}
/// <summary>
/// Declared so <see cref="AddPriceSource{T}"/> is reachable from tests; a local function would not be.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Registers a source's typed client with its resilience pipeline and QuotaHandler, then exposes
    /// it as <see cref="IPriceSource"/>. Auth is the only per-provider part (D-12).
    /// </summary>
    internal static void AddPriceSource<T>(
        IServiceCollection services,
        string sourceCode,
        Action<IHttpClientBuilder, Func<IServiceProvider, PriceSourceOptions>> configureAuth)
        where T : class, IPriceSource
    {
        PriceSourceOptions GetOptions(IServiceProvider sp) =>
            sp.GetRequiredService<IOptions<PriceSourcesOptions>>().Value.RequireByCode(sourceCode);

        var clientBuilder = services.AddHttpClient<T>((sp, http) =>
        {
            var options = GetOptions(sp);
            http.BaseAddress = options.BaseUrl;
            // Off, so every timeout comes from inside the pipeline where retry can see it (D-13).
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Must stay above QuotaHandler so every retry attempt is charged its own lease (D-7, D-13).
        // Not chained: AddResilienceHandler does not return IHttpClientBuilder.
        clientBuilder.AddResilienceHandler("price-source", (pipeline, context) =>
        {
            var options = GetOptions(context.ServiceProvider);

            var resilience = context.ServiceProvider
                .GetRequiredService<IOptions<PriceFeedResilienceOptions>>().Value;

            // Outermost: caps the whole retry sequence.
            pipeline.AddTimeout(options.TotalTimeout);

            //Polly refuses zero retries, so one attempt means no retry step (D-15)
            if (resilience.MaxAttempts > 1)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    // PriceSourceException is thrown above this pipeline, so it can never be seen here
                    // (D-14). QuotaExhaustedException is a failover signal, not a retry signal.
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<TimeoutRejectedException>()
                        .Handle<HttpRequestException>()

                        // A failing status is a result, not an exception; without this a 503 is never
                        // retried. 429/402 arrive as QuotaExhaustedException via QuotaHandler.
                        .HandleResult(response =>
                            response.StatusCode is HttpStatusCode.RequestTimeout
                                                or >= HttpStatusCode.InternalServerError),
                    // Polly counts retries, the setting counts attempts. Convert here and nowhere else (D-15).
                    MaxRetryAttempts = resilience.MaxAttempts - 1,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = resilience.RetryBackoffBase,

                    // Must stay false: this options type defaults it to true, and jitter breaks the
                    // schedule MinimumTotalTimeout models (D-15).
                    UseJitter = false,
                });
            }

            // Innermost: a fresh budget per attempt. A timed-out attempt has still spent its lease (D-7).
            pipeline.AddTimeout(options.RequestTimeout);
        });

        clientBuilder.AddHttpMessageHandler(sp => new QuotaHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sourceCode,
            sp.GetRequiredService<IAppLogService<QuotaHandler>>()));

        configureAuth(clientBuilder, GetOptions);

        // Transient, matching the typed client's own lifetime.
        services.AddTransient<IPriceSource>(sp => sp.GetRequiredService<T>());
        services.AddSingleton(new RegisteredPriceSource(sourceCode));
    }


}
