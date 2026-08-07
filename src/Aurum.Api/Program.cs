using Aurum.Api.Modules.Pricing;
using Aurum.Api.Shared;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Aurum");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Aurum is not configured.");
}

builder.Services.AddDbContext<AurumDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddPricingModule(builder.Configuration);

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
