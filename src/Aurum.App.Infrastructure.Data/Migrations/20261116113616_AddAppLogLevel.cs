using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppLogLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "app_logs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Fills existing rows; the app always writes a level, so nothing new gets this.
                defaultValue: "Information");

            // Only LogErrorAsync sets ExceptionType, and it always means Error.
            migrationBuilder.Sql(
                """UPDATE app_logs SET "Level" = 'Error' WHERE "ExceptionType" IS NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Level",
                table: "app_logs");
        }
    }
}
