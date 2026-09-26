using System.Net;
using Aurum.Api;
using Aurum.App.Application.AppLogs;
using Aurum.App.Application.Common.Behaviors;
using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Repositories;
using Aurum.App.Infrastructure.Pricing;
using FluentValidation;
using MediatR;
using Aurum.App.Infrastructure.Pricing.Jobs;
using Aurum.App.Infrastructure.Pricing.Quota;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;
using Serilog;

// Every service registration in the application lives in this file. There is no Add*Module
// extension method and no ServiceCollectionExtensions per layer: D-5 recorded the folder-module
// seam, and D-5 now records its reversal and what that costs (an InternalsVisibleTo to this
// assembly, and a file that grows with every feature).

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddOpenApi();
builder.Services.AddControllers();

// The one authority for every timestamp the application writes — not just quota period
// boundaries. AurumDbContext takes it as a required constructor parameter so no construction
// site can fall back to wall clock; that fallback is what once let ApplyAuditFields and the
// governor stamp two rows in the same table from two different clocks (D-7).
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Aurum");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Aurum is not configured.");
}

builder.Services.AddDbContext<AurumDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// ── CQRS ──────────────────────────────────────────────────────────────────────────────────────

// Handlers and validators are discovered by scanning Aurum.App.Application. Both scans are
// anchored on a type in that assembly rather than on Assembly.GetExecutingAssembly(), which here
// would be Aurum.Api and would find nothing.
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<LoggingBehavior<IRequest<Unit>, Unit>>());

builder.Services.AddValidatorsFromAssemblyContaining<ValidationBehavior<IRequest<Unit>, Unit>>();

// Order is the pipeline: Logging wraps Validation wraps Transaction wraps the handler. Logging is
// outermost so its duration covers validation and the commit, and so a ValidationException is
// recorded rather than escaping unlogged. Transaction is innermost of the three so a validation
// failure never opens a transaction.
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

// ── Application logging ───────────────────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContextAccessor, HttpRequestContextAccessor>();

// Singleton because it owns the channel: a scoped queue would be a fresh, empty channel per
// request, so every enqueued row would be dropped on the floor when the scope ended.
builder.Services.AddSingleton<IAppLogQueue, AppLogQueue>();
builder.Services.AddScoped(typeof(IAppLogService<>), typeof(AppLogService<>));
builder.Services.AddHostedService<AppLogDrainService>();

// ── Pricing options ───────────────────────────────────────────────────────────────────────────

builder.Services.AddOptions<PriceSourcesOptions>()
    // Bound onto the dictionary rather than the options object so the section's children are the
    // map's entries. Keeps the config paths operators already use (PriceSources:GoldApiIo:ApiKey,
    // PriceSources__GoldApiIo__ApiKey) while making the lookup by source code a data lookup
    // rather than a switch.
    .Configure<IConfiguration>((options, cfg) =>
        cfg.GetSection(PriceSourcesOptions.SectionName).Bind(options.Sources))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PricePollingOptions>()
    .BindConfiguration(PricePollingOptions.SectionName)
    .Validate(
        o => o.PollInterval > TimeSpan.Zero,
        $"{PricePollingOptions.SectionName}:PollInterval must be set to a positive interval.")
    .ValidateOnStart();

builder.Services.AddOptions<PriceFeedCircuitOptions>()
    .BindConfiguration(PriceFeedCircuitOptions.SectionName)
    .ValidateOnStart();

// ValidateDataAnnotations() does not descend into nested objects, so the [Required] on ApiKey and
// the [Range] on MonthlyRequestLimit are enforced by this validator and nowhere else (D-9).
builder.Services.AddSingleton<IValidateOptions<PriceSourcesOptions>, PriceSourcesOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<PriceFeedCircuitOptions>, PriceFeedCircuitOptionsValidator>();

// ── Pricing services ──────────────────────────────────────────────────────────────────────────

// Singleton: circuit state must outlive a poll, and this object holds only strings, ints and
// timestamps plus the clock — nothing HTTP-bearing for IHttpClientFactory to recycle underneath
// it, which is the constraint D-12 imposed on the sources themselves.
builder.Services.AddSingleton<SourceCircuitStore>();

builder.Services.AddScoped<IQuotaGovernor, PostgresQuotaGovernor>();

// Scoped, not singleton: it holds IEnumerable<IPriceSource>, and those are typed clients. A
// singleton feed would capture handlers the factory recycles — D-12's rejected alternative one
// level up.
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

// /health is liveness: is the process up. /health/ready gates traffic on dependencies —
// tagged so the two endpoints cannot silently drift as checks are added.
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapHealthChecks("/health", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

await MigrateAsync(app);

app.Run();

// Migrating at startup is right for a single instance and wrong the moment the API is scaled
// out (concurrent migrations race). Phase 5's replica work is when this becomes a separate step.
static async Task MigrateAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AurumDbContext>();
    await db.Database.MigrateAsync();
}
/// <summary>
/// The entry point. Declared explicitly so the registration helper below is a member of it rather
/// than a local function — a local function in top-level statements is unreachable from the test
/// assembly, and the wiring order inside <see cref="AddPriceSource{T}"/> is the thing under test.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Registers a source's typed client with its resilience pipeline and QuotaHandler, then projects
    /// it onto <see cref="IPriceSource"/> so the failover chain can resolve
    /// <c>IEnumerable&lt;IPriceSource&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A member of Program rather than an extension method in the pricing assembly, because every
    /// registration in this application is in this file. It is not a local function for one reason:
    /// the wiring order below is the subject of <c>ResilienceWiringTests</c>, and a local function
    /// in top-level statements cannot be called from the test assembly.
    ///
    /// The handler is attached here rather than at each call site because a source registered
    /// without it still compiles, still works, and spends its budget uncounted — the one wiring
    /// mistake that produces no symptom until the provider starts rejecting requests.
    ///
    /// <paramref name="configureAuth"/> exists because auth is the only genuinely per-provider part:
    /// GoldAPI uses x-access-token, API Ninjas X-Api-Key, and MetalpriceAPI a query parameter.
    /// </remarks>
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
            // Infinite here so the ONLY cancellation comes from inside the pipeline, where the retry
            // predicate can observe it. HttpClient.Timeout is a TOTAL budget applied outside every
            // handler: it would cancel attempt three for the seconds attempts one and two spent, and
            // surface as a bare TaskCanceledException that ShouldHandle never sees and the failover
            // chain cannot attribute to a source (D-13). The real ceiling is TotalTimeout below.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Registered before QuotaHandler so the retry loop sits *above* the governor: each attempt
        // passes through the handler and is charged its own lease, because that is what the provider
        // bills. Inverting the two would retry below the accounting and spend the budget uncounted —
        // an under-count, the direction that costs a month rather than a poll (D-7).
        //
        // Split out of the fluent chain because AddResilienceHandler returns
        // IHttpResiliencePipelineBuilder, not IHttpClientBuilder — nothing can be chained after it.
        clientBuilder.AddResilienceHandler("price-source", (pipeline, context) =>
        {
            var options = GetOptions(context.ServiceProvider);

            // Outermost: the whole retry sequence can't run away forever.
            pipeline.AddTimeout(options.TotalTimeout);

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                // TimeoutRejectedException is what AddTimeout throws — a real, typed exception the
                // predicate can see, unlike the TaskCanceledException http.Timeout produced.
                //
                // PriceSourceException is ABSENT rather than excluded, and its absence is not an
                // oversight: every source parses the body above the whole handler chain, so a
                // garbage-200 throws after this pipeline has already returned success. This predicate
                // can never observe one. That blind spot is half of why the breaker is hand-rolled in
                // FailoverPriceFeed (D-14).
                //
                // QuotaExhaustedException is absent for a different reason: QuotaHandler raises it
                // below us, and another attempt cannot succeed until the period rolls while
                // QuotaHandler charges for it either way. A failover signal, not a retry signal.
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<HttpRequestException>()

                    // A failing status is a RESULT, not an exception: HttpClient.GetAsync returns a
                    // 500, it does not throw one. Without this clause the two Handle<T>() lines above
                    // cover only transport-level failures, and a provider answering 503 all day was
                    // never retried at all — the strategy read as configured and did nothing on the
                    // most common failure it exists for. Caught by
                    // ResilienceWiringTests.Each_retry_attempt_spends_its_own_lease.
                    //
                    // 429 and 402 deliberately absent: QuotaHandler converts those below this
                    // pipeline, after clamping the period, so they arrive as QuotaExhaustedException
                    // and are a failover signal rather than a retry signal.
                    .HandleResult(response =>
                        response.StatusCode is HttpStatusCode.RequestTimeout
                                            or >= HttpStatusCode.InternalServerError),
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
            });

            // Innermost: a fresh budget for EACH attempt, because it's inside the retry loop. This
            // sits ABOVE QuotaHandler, so a timed-out attempt has already spent its lease — correct
            // under D-7's no-refunds rule, and the reason a low timeout is a budget decision.
            pipeline.AddTimeout(options.RequestTimeout);
        });

        clientBuilder.AddHttpMessageHandler(sp => new QuotaHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sourceCode,
            sp.GetRequiredService<ILogger<QuotaHandler>>()));

        configureAuth(clientBuilder, GetOptions);

        // Transient, matching the typed client's own lifetime. Resolving IEnumerable<IPriceSource>
        // then yields every registered source; the chain orders them by Priority.
        services.AddTransient<IPriceSource>(sp => sp.GetRequiredService<T>());
        services.AddSingleton(new RegisteredPriceSource(sourceCode));
    }


}
