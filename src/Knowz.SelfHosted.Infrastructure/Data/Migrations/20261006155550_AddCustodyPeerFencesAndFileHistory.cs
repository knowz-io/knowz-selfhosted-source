using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyPeerFencesAndFileHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityRevision",
                table: "VaultSyncLinks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FileVersion",
                table: "ShSealedFiles",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentVersion",
                table: "ShSealedFiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousFileRecordId",
                table: "ShSealedFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RootFileRecordId",
                table: "ShSealedFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VaultId",
                table: "ShSealedFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentBindingSha256",
                table: "ShArchivePeerBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorNamespace",
                table: "ShArchiveImportJournals",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PeerBindingSha256",
                table: "ShArchiveImportJournals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityRevision",
                table: "PlatformConnections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FileVersion",
                table: "FileRecords",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentVersion",
                table: "FileRecords",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousFileRecordId",
                table: "FileRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RootFileRecordId",
                table: "FileRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShCryptoContextImportAnchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappingSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlanSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTrust = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublicKeyCatalogJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShCryptoContextImportAnchors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShCryptoCustodyChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyStateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlanSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BindingJson = table.Column<string>(type: "text", nullable: false),
                    ExpectedProofSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShCryptoCustodyChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShRecoveredKeyCustodyAcceptances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyStateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTrust = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShRecoveredKeyCustodyAcceptances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShCryptoContextImportAnchors_TenantId_PlanId_ContextId",
                table: "ShCryptoContextImportAnchors",
                columns: new[] { "TenantId", "PlanId", "ContextId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShCryptoCustodyChallenges_TenantId_ActorId_ExpiresAt",
                table: "ShCryptoCustodyChallenges",
                columns: new[] { "TenantId", "ActorId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShRecoveredKeyCustodyAcceptances_TenantId_ContextId_KeyId",
                table: "ShRecoveredKeyCustodyAcceptances",
                columns: new[] { "TenantId", "ContextId", "KeyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShCryptoContextImportAnchors");

            migrationBuilder.DropTable(
                name: "ShCryptoCustodyChallenges");

            migrationBuilder.DropTable(
                name: "ShRecoveredKeyCustodyAcceptances");

            migrationBuilder.DropColumn(
                name: "SecurityRevision",
                table: "VaultSyncLinks");

            migrationBuilder.DropColumn(
                name: "FileVersion",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "IsCurrentVersion",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "PreviousFileRecordId",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "RootFileRecordId",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "VaultId",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "ParentBindingSha256",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "ActorNamespace",
                table: "ShArchiveImportJournals");

            migrationBuilder.DropColumn(
                name: "PeerBindingSha256",
                table: "ShArchiveImportJournals");

            migrationBuilder.DropColumn(
                name: "SecurityRevision",
                table: "PlatformConnections");

            migrationBuilder.DropColumn(
                name: "FileVersion",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "IsCurrentVersion",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "PreviousFileRecordId",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "RootFileRecordId",
                table: "FileRecords");
        }
    }
}
