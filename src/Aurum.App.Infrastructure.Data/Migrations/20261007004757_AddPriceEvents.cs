using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    // price_events is deliberately an ordinary table, not a hypertable (D-18). It gets a few dozen
    // rows a day, and events must outlive price_ticks' 30-day retention: a hypertable invites
    // someone to add the same retention policy "for consistency" and silently delete the history.
    /// <inheritdoc />
    public partial class AddPriceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    WindowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowEndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StartMid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    EndMid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DeltaAbsolute = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DeltaPercent = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    VelocityPercentPerMinute = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Volatility = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaselineSourceCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsCrossSource = table.Column<bool>(type: "boolean", nullable: false),
                    ThresholdProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TriggeredRule = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_price_events_price_sources_BaselineSourceCode",
                        column: x => x.BaselineSourceCode,
                        principalTable: "price_sources",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_price_events_price_sources_SourceCode",
                        column: x => x.SourceCode,
                        principalTable: "price_sources",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_price_events_BaselineSourceCode",
                table: "price_events",
                column: "BaselineSourceCode");

            migrationBuilder.CreateIndex(
                name: "IX_price_events_SourceCode",
                table: "price_events",
                column: "SourceCode");

            migrationBuilder.CreateIndex(
                name: "IX_price_events_Symbol_WindowCode_WindowEndedAt",
                table: "price_events",
                columns: new[] { "Symbol", "WindowCode", "WindowEndedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_events");
        }
    }
}
