using Aurum.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// Guards the parts of the schema EF's model snapshot cannot see. Without these, someone
/// regenerates the initial migration, loses the raw SQL, and nothing fails until the tick
/// table is too large to convert.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PriceTicks_is_a_hypertable_with_daily_chunks()
    {
        await using var db = fixture.CreateDbContext();

        var interval = await db.Database
            .SqlQuery<long?>($"""
                SELECT d.interval_length AS "Value"
                  FROM timescaledb_information.dimensions d
                 WHERE d.hypertable_name = 'price_ticks'
                   AND d.column_name = 'ObservedAt'
                """)
            .SingleOrDefaultAsync();

        Assert.NotNull(interval);
        // interval_length is microseconds for a time dimension.
        Assert.Equal((long)TimeSpan.FromDays(1).TotalMicroseconds, interval);
    }

    [Fact]
    public async Task PriceTicks_has_a_thirty_day_retention_policy()
    {
        await using var db = fixture.CreateDbContext();

        var count = await db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                  FROM timescaledb_information.jobs
                 WHERE hypertable_name = 'price_ticks'
                   AND proc_name = 'policy_retention'
                """)
            .SingleAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Pgvector_is_available_for_phase_two()
    {
        await using var db = fixture.CreateDbContext();

        var installed = await db.Database
            .SqlQuery<bool>($"""SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') AS "Value" """)
            .SingleAsync();

        Assert.True(installed, "D-3 chose timescaledb-ha specifically so pgvector ships with the image.");
    }
}
