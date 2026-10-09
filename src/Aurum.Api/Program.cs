using System.Net;
using Aurum.Api;
using Aurum.Api.Hubs;
using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Common.Behaviors;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Repositories;
using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Cache;
using Aurum.App.Infrastructure.Pricing.Deltas;
using FluentValidation;
using MediatR;
using Aurum.App.Infrastructure.Pricing.Jobs;
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

var connectionString = builder.Configuration.GetConnectionString("Aurum");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Aurum is not configured.");
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
builder.Services.AddScoped<IRequestContextAccessor, HttpRequestContextAccessor>();

// Singleton because it owns the channel; a scoped queue would drop its rows when the scope ends.
builder.Services.AddSingleton<IAppLogQueue, AppLogQueue>();
builder.Services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>));
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
builder.Services.AddSingleton<SourceCircuitStore>();
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

            // Innermost: a fresh budget per attempt. A timed-out attempt has still spent its lease (D-7).
            pipeline.AddTimeout(options.RequestTimeout);
        });

        clientBuilder.AddHttpMessageHandler(sp => new QuotaHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sourceCode,
            sp.GetRequiredService<ILogger<QuotaHandler>>()));

        configureAuth(clientBuilder, GetOptions);

        // Transient, matching the typed client's own lifetime.
        services.AddTransient<IPriceSource>(sp => sp.GetRequiredService<T>());
        services.AddSingleton(new RegisteredPriceSource(sourceCode));
    }


}
