using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropPriceSourceIsEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing read or wrote it; settings decide whether a source is used (D-20).
            migrationBuilder.DropIndex(
                name: "IX_price_sources_IsEnabled_Priority",
                table: "price_sources");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "price_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "price_sources",
                type: "boolean",
                nullable: false,
                // Every seed wrote TRUE; false would mark every source switched off on rollback.
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_sources_IsEnabled_Priority",
                table: "price_sources",
                columns: new[] { "IsEnabled", "Priority" });
        }
    }
}
