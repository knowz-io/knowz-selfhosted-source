using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArchivePeerBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShArchivePeerBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    BindingSlotSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignerFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignerPublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CredentialIdentitySha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlatformConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrustProvenance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    BindingRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShArchivePeerBindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShArchivePeerBindings_TenantId_BindingSlotSha256",
                table: "ShArchivePeerBindings",
                columns: new[] { "TenantId", "BindingSlotSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShArchivePeerBindings_TenantId_DestinationVaultId",
                table: "ShArchivePeerBindings",
                columns: new[] { "TenantId", "DestinationVaultId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShArchivePeerBindings");
        }
    }
}
