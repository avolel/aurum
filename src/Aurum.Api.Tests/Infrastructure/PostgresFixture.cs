using Aurum.App.Infrastructure.Data;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// A real Postgres with TimescaleDB and pgvector, migrated once per test collection.
/// </summary>
/// <remarks>
/// Deliberately not an in-memory or SQLite provider: hypertables and vector columns behave
/// differently enough from vanilla Postgres that a mock would validate nothing. This is the
/// same image compose runs, so a schema change that breaks Timescale breaks the tests too.
/// </remarks>
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
    public AurumDbContext CreateDbContext(TimeProvider? clock = null)
    {
        var options = new DbContextOptionsBuilder<AurumDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AurumDbContext(options, clock ?? TimeProvider.System);
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
