using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aurum.Api.Migrations
{
    /// <summary>
    /// Registers <c>api-ninjas</c> and <c>metalprice-api</c> in <c>price_sources</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema-free: this exists only because <c>price_ticks.SourceCode</c> is a foreign key to
    /// <c>price_sources.Code</c>. Both sources are configured and enabled in
    /// <c>appsettings.json</c>, but <c>PricePollingService</c> selects the lowest
    /// <c>Priority</c> unconditionally, so <c>goldapi.io</c> has served every poll so far and the
    /// missing rows have never been reached. The first failover (phase-1-todo item 3) would insert
    /// a tick carrying an unregistered code, and <c>SaveChangesAsync</c> would throw a foreign-key
    /// violation *after* the request was sent and the quota lease spent — a lost tick and a lost
    /// request charged to a provider that answered correctly.
    /// </para>
    /// <para>
    /// <c>now()</c> rather than an injected timestamp: the <c>TimeProvider</c> invariant governs
    /// what the *application* writes, and a migration runs outside the host that owns that clock —
    /// no fake clock can reach here. This matches the seed in <c>InitialSchema</c>.
    /// </para>
    /// <para>
    /// <c>Priority</c> and <c>IsEnabled</c> are seeded to match <c>appsettings.json</c> but are
    /// descriptive, not authoritative: the chain orders on <c>IPriceSource.Priority</c>, which is
    /// bound from configuration. These columns exist so an operator reading the table sees the
    /// intended shape of the chain; they can drift from config and nothing reads them to decide.
    /// </para>
    /// </remarks>
    public partial class SeedAdditionalPriceSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seeded as SQL rather than HasData for the reason stated in InitialSchema: operational
            // columns (LastSuccessAt, LastFailureReason) written by the poller must never be
            // reverted by a later migration re-applying seed data. ON CONFLICT DO NOTHING keeps
            // this idempotent against a database where the rows were added by hand.
            migrationBuilder.Sql("""
                INSERT INTO price_sources ("Code", "DisplayName", "Priority", "IsEnabled", "CreatedAt", "UpdatedAt")
                VALUES ('api-ninjas',     'API Ninjas',    2, TRUE, now(), now()),
                       ('metalprice-api', 'MetalpriceAPI', 3, TRUE, now(), now())
                ON CONFLICT ("Code") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately skips a row that already has ticks. An unconditional DELETE would hit
            // the price_ticks foreign key and abort the rollback partway through, leaving the
            // database in a state neither migration describes. A retained row is inert — nothing
            // reads price_sources to choose a source — whereas a failed rollback is not.
            migrationBuilder.Sql("""
                DELETE FROM price_sources ps
                WHERE ps."Code" IN ('api-ninjas', 'metalprice-api')
                  AND NOT EXISTS (
                      SELECT 1 FROM price_ticks pt WHERE pt."SourceCode" = ps."Code"
                  );
                """);
        }
    }
}
