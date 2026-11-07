using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompleteEncryptedGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId",
                table: "ShSealedKnowledge");

            migrationBuilder.DropIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId",
                table: "ShSealedComments");

            migrationBuilder.DropIndex(
                name: "IX_ShCryptoKeyIdentities_TenantId_PortableCryptoContextId_Fing~",
                table: "ShCryptoKeyIdentities");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryWriteFingerprint",
                table: "ShVaultCryptoContextLinks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryWriteGeneration",
                table: "ShVaultCryptoContextLinks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LineageScope",
                table: "ShSealedKnowledge",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BinarySha256",
                table: "ShSealedFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BlobStorageId",
                table: "ShSealedFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalFileId",
                table: "ShSealedFiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "LineageScope",
                table: "ShSealedComments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceLifecycle",
                table: "ShCryptoKeyIdentities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ShArchiveImportJournals",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "text", nullable: false),
                    MappingSha256 = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StagedObjectIdsJson = table.Column<string>(type: "text", nullable: false),
                    CommitId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreservedPayloadCount = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShArchiveImportJournals", x => x.PlanId);
                });

            migrationBuilder.CreateTable(
                name: "ShArchiveSigningIdentities",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShArchiveSigningIdentities", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "ShFileObjectReferences",
                columns: table => new
                {
                    FileRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobStorageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShFileObjectReferences", x => x.FileRecordId);
                });

            migrationBuilder.CreateTable(
                name: "ShPayloadKeyReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    FieldName = table.Column<string>(type: "text", nullable: true),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: true),
                    LegacyUnattributed = table.Column<bool>(type: "boolean", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: true),
                    OriginalBindingKind = table.Column<string>(type: "text", nullable: false),
                    OriginalBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    KeyType = table.Column<string>(type: "text", nullable: false),
                    AlgorithmProfile = table.Column<string>(type: "text", nullable: false),
                    EnvelopeVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShPayloadKeyReferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShSealedAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalAttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    InboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    EncryptedFieldsJson = table.Column<string>(type: "text", nullable: true),
                    ExcludeFromAiProcessing = table.Column<bool>(type: "boolean", nullable: false),
                    ExcludeFromVersionDiff = table.Column<bool>(type: "boolean", nullable: false),
                    AttachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedAttachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShSealedFileStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<string>(type: "text", nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BlobStorageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedFileStages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId_LineageScope",
                table: "ShSealedKnowledge",
                columns: new[] { "TenantId", "OriginalItemId", "LineageScope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId_LineageScope",
                table: "ShSealedComments",
                columns: new[] { "TenantId", "OriginalCommentId", "LineageScope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShCryptoKeyIdentities_TenantId_PortableCryptoContextId_Fing~",
                table: "ShCryptoKeyIdentities",
                columns: new[] { "TenantId", "PortableCryptoContextId", "Fingerprint", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShArchiveImportJournals_TenantId_Status_UpdatedAt",
                table: "ShArchiveImportJournals",
                columns: new[] { "TenantId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShPayloadKeyReferences_TenantId_OwnerKind_OwnerId_Role_Fiel~",
                table: "ShPayloadKeyReferences",
                columns: new[] { "TenantId", "OwnerKind", "OwnerId", "Role", "FieldName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedAttachments_TenantId_FileRecordId",
                table: "ShSealedAttachments",
                columns: new[] { "TenantId", "FileRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedFileStages_TenantId_ActorId_IdempotencyKey",
                table: "ShSealedFileStages",
                columns: new[] { "TenantId", "ActorId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShArchiveImportJournals");

            migrationBuilder.DropTable(
                name: "ShArchiveSigningIdentities");

            migrationBuilder.DropTable(
                name: "ShFileObjectReferences");

            migrationBuilder.DropTable(
                name: "ShPayloadKeyReferences");

            migrationBuilder.DropTable(
                name: "ShSealedAttachments");

            migrationBuilder.DropTable(
                name: "ShSealedFileStages");

            migrationBuilder.DropIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId_LineageScope",
                table: "ShSealedKnowledge");

            migrationBuilder.DropIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId_LineageScope",
                table: "ShSealedComments");

            migrationBuilder.DropIndex(
                name: "IX_ShCryptoKeyIdentities_TenantId_PortableCryptoContextId_Fing~",
                table: "ShCryptoKeyIdentities");

            migrationBuilder.DropColumn(
                name: "PrimaryWriteFingerprint",
                table: "ShVaultCryptoContextLinks");

            migrationBuilder.DropColumn(
                name: "PrimaryWriteGeneration",
                table: "ShVaultCryptoContextLinks");

            migrationBuilder.DropColumn(
                name: "LineageScope",
                table: "ShSealedKnowledge");

            migrationBuilder.DropColumn(
                name: "BinarySha256",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "BlobStorageId",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "OriginalFileId",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "LineageScope",
                table: "ShSealedComments");

            migrationBuilder.DropColumn(
                name: "SourceLifecycle",
                table: "ShCryptoKeyIdentities");

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId",
                table: "ShSealedKnowledge",
                columns: new[] { "TenantId", "OriginalItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId",
                table: "ShSealedComments",
                columns: new[] { "TenantId", "OriginalCommentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShCryptoKeyIdentities_TenantId_PortableCryptoContextId_Fing~",
                table: "ShCryptoKeyIdentities",
                columns: new[] { "TenantId", "PortableCryptoContextId", "Fingerprint" },
                unique: true);
        }
    }
}
