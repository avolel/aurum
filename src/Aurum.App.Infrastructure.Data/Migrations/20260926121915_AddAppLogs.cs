using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LogType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ExceptionType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StackTrace = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HttpMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_logs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_logs_CorrelationId",
                table: "app_logs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_app_logs_CreatedAt",
                table: "app_logs",
                column: "CreatedAt",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_logs");
        }
    }
}
