using Aurum.App.Infrastructure.Data;
using Aurum.App.Infrastructure.Data.Entities.Pricing;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// The same TimescaleDB image compose runs, migrated once per test collection. Not an in-memory
/// provider: hypertables and vector columns would not be tested at all.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("aurum_test")
        .WithUsername("aurum")
        .WithPassword("aurum")
        // The HA image bootstraps via Patroni and is slow to accept connections; the default
        // "port is open" wait strategy lets tests start against a database that then vanishes.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready -U aurum -d aurum_test"))
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// The real time, cut to the whole minute. Stored ticks must be dated near it: the 30-day
    /// retention job on <c>price_ticks</c> runs on the server's real clock and drops older rows mid-test.
    /// </summary>
    public static DateTimeOffset RecentMinute
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        }
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The compose stack creates extensions from ops/db/init as superuser. Testcontainers
        // does not mount that, so do it here — same statements, same ordering guarantee.
        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS timescaledb; CREATE EXTENSION IF NOT EXISTS vector;");

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// A context over the fixture's database. Pass <paramref name="clock"/> when the test needs
    /// EF-written audit stamps to agree with the clock the governor writes its own rows under.
    /// </summary>
    public AurumDbContext CreateDbContext(TimeProvider? clock = null, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AurumDbContext>()
            .UseNpgsql(ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new AurumDbContext(builder.Options, clock ?? TimeProvider.System);
    }

    /// <summary>Stores ticks with <c>ReceivedAt</c> equal to <c>ObservedAt</c>. Source codes must be seeded ones.</summary>
    public async Task SeedTicksAsync(
        params (string Symbol, DateTimeOffset ObservedAt, decimal Mid, string SourceCode)[] ticks)
    {
        await using var db = CreateDbContext();

        foreach (var (symbol, observedAt, mid, sourceCode) in ticks)
        {
            db.PriceTicks.Add(new PriceTick
            {
                Symbol = symbol,
                ObservedAt = observedAt,
                ReceivedAt = observedAt,
                Mid = mid,
                SourceCode = sourceCode,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        var result = await _container.ExecScriptAsync(sql);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture setup SQL failed: {result.Stderr}");
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
