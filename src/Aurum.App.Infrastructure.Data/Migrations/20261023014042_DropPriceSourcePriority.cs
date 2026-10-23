using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aurum.App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropPriceSourcePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing read it, and it had drifted from settings, which decide the order (D-20).
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "price_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "price_sources",
                type: "integer",
                nullable: false,
                // The seeded values are lost; any value is safe because nothing reads the column (D-20).
                defaultValue: 0);
        }
    }
}
