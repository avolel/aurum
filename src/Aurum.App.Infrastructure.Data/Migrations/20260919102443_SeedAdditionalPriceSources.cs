using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Registers <c>api-ninjas</c> and <c>metalprice-api</c> in <c>price_sources</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema-free: this exists only because <c>price_ticks.SourceCode</c> is a foreign key to
    /// <c>price_sources.Code</c>. When this was written, <c>PricePollingService</c> always picked
    /// <c>goldapi.io</c>, so the missing rows had never been reached. The first failover
    /// (phase-1-todo item 3) would have inserted
    /// a tick carrying an unregistered code, and <c>SaveChangesAsync</c> would have thrown a foreign-key
    /// violation *after* the request was sent and the quota lease spent — a lost tick and a lost
    /// request charged to a provider that answered correctly.
    /// </para>
    /// <para>
    /// <c>now()</c> rather than an injected timestamp: the <c>TimeProvider</c> invariant governs
    /// what the *application* writes, and a migration runs outside the host that owns that clock —
    /// no fake clock can reach here. This matches the seed in <c>InitialSchema</c>.
    /// </para>
    /// <para>
    /// The <c>Priority</c> and <c>IsEnabled</c> columns seeded here were later dropped
    /// (<c>DropPriceSourceIsEnabled</c>, <c>DropPriceSourcePriority</c>; D-20). Configuration alone
    /// sets whether a source is used and in what order (<c>PriceSourcesOptions.EnabledInFailoverOrder</c>).
    /// This SQL must stay as it is: it runs before those drops on a fresh database.
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
