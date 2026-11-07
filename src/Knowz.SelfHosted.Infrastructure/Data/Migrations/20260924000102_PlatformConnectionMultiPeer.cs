using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlatformConnectionMultiPeer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_TenantId",
                table: "PlatformConnections");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_TenantId",
                table: "PlatformConnections",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_TenantId_PlatformApiUrl",
                table: "PlatformConnections",
                columns: new[] { "TenantId", "PlatformApiUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_TenantId",
                table: "PlatformConnections");

            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_TenantId_PlatformApiUrl",
                table: "PlatformConnections");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_TenantId",
                table: "PlatformConnections",
                column: "TenantId",
                unique: true);
        }
    }
}
