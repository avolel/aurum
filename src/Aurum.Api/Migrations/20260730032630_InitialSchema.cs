using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aurum.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:timescaledb", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "api_quota_windows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodStartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestLimit = table.Column<int>(type: "integer", nullable: false),
                    RequestsUsed = table.Column<int>(type: "integer", nullable: false),
                    ProviderRejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_quota_windows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "macro_series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Units = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Frequency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_macro_series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "price_sources",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_sources", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "macro_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MacroSeriesId = table.Column<int>(type: "integer", nullable: false),
                    ObservedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_macro_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_macro_observations_macro_series_MacroSeriesId",
                        column: x => x.MacroSeriesId,
                        principalTable: "macro_series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_ticks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Bid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Ask = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Mid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_ticks", x => new { x.ObservedAt, x.Id });
                    table.ForeignKey(
                        name: "FK_price_ticks_price_sources_SourceCode",
                        column: x => x.SourceCode,
                        principalTable: "price_sources",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_quota_windows_SourceCode_PeriodKey",
                table: "api_quota_windows",
                columns: new[] { "SourceCode", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_macro_observations_MacroSeriesId_ObservedOn",
                table: "macro_observations",
                columns: new[] { "MacroSeriesId", "ObservedOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_macro_series_Provider_Code",
                table: "macro_series",
                columns: new[] { "Provider", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_sources_IsEnabled_Priority",
                table: "price_sources",
                columns: new[] { "IsEnabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_price_ticks_SourceCode",
                table: "price_ticks",
                column: "SourceCode");

            migrationBuilder.CreateIndex(
                name: "IX_price_ticks_Symbol_ObservedAt",
                table: "price_ticks",
                columns: new[] { "Symbol", "ObservedAt" },
                descending: new[] { false, true });

            // --- TimescaleDB ---------------------------------------------------------------
            // Hand-written because EF has no model concept for hypertables, and it belongs in
            // the FIRST migration on purpose: create_hypertable on a populated table is a data
            // migration, on an empty one it is a DDL statement. Daily chunks and 30-day hot
            // retention per §11.
            //
            // EF's model snapshot knows nothing about any of this, so a later Add-Migration will
            // not try to revert it — but the integration tests must assert it is still in place.
            migrationBuilder.Sql("""
                SELECT create_hypertable(
                    'price_ticks',
                    'ObservedAt',
                    chunk_time_interval => INTERVAL '1 day',
                    if_not_exists => TRUE
                );
                """);

            migrationBuilder.Sql("""
                SELECT add_retention_policy('price_ticks', INTERVAL '30 days', if_not_exists => TRUE);
                """);

            // --- Seed ----------------------------------------------------------------------
            // Seeded here rather than via HasData so that operational columns (LastSuccessAt)
            // written by the poller are never reverted by a later migration re-applying seed data.
            migrationBuilder.Sql("""
                INSERT INTO price_sources ("Code", "DisplayName", "Priority", "IsEnabled", "CreatedAt", "UpdatedAt")
                VALUES ('goldapi.io', 'GoldAPI.io', 1, TRUE, now(), now())
                ON CONFLICT ("Code") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_quota_windows");

            migrationBuilder.DropTable(
                name: "macro_observations");

            migrationBuilder.DropTable(
                name: "price_ticks");

            migrationBuilder.DropTable(
                name: "macro_series");

            migrationBuilder.DropTable(
                name: "price_sources");
        }
    }
}
