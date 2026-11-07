using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShSealedVaultStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShCryptoKeyIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortableCryptoContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    KeyType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Custody = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShCryptoKeyIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShPortableCryptoContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BindingKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginalBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlgorithmProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProvenanceStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShPortableCryptoContexts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShSealedFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    BinaryContentBase64 = table.Column<string>(type: "text", nullable: true),
                    EncryptionKeyRef = table.Column<string>(type: "text", nullable: true),
                    EnvelopeVersion = table.Column<int>(type: "integer", nullable: true),
                    EncryptedDek = table.Column<string>(type: "text", nullable: true),
                    EncryptedDekNonce = table.Column<string>(type: "text", nullable: true),
                    EphemeralPublicKey = table.Column<string>(type: "text", nullable: true),
                    ContentNonce = table.Column<string>(type: "text", nullable: true),
                    EncryptedFieldsJson = table.Column<string>(type: "text", nullable: true),
                    CryptoBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShSealedKnowledge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    EnvelopeVersion = table.Column<int>(type: "integer", nullable: true),
                    EncryptedDek = table.Column<string>(type: "text", nullable: true),
                    EncryptedDekNonce = table.Column<string>(type: "text", nullable: true),
                    EphemeralPublicKey = table.Column<string>(type: "text", nullable: true),
                    ContentNonce = table.Column<string>(type: "text", nullable: true),
                    EncryptionKeyRef = table.Column<string>(type: "text", nullable: true),
                    EncryptedFieldsJson = table.Column<string>(type: "text", nullable: true),
                    WrappedDek = table.Column<string>(type: "text", nullable: true),
                    DekNonce = table.Column<string>(type: "text", nullable: true),
                    CryptoVersion = table.Column<string>(type: "text", nullable: true),
                    CryptoBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlgorithmProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LifecycleStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerifiedFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ClientVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedKnowledge", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShSealedVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalKnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<long>(type: "bigint", nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    EnvelopeMetadata = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShVaultCryptoContextLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortableCryptoContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimaryWriteContext = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShVaultCryptoContextLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShCryptoKeyIdentities_TenantId_PortableCryptoContextId_Fing~",
                table: "ShCryptoKeyIdentities",
                columns: new[] { "TenantId", "PortableCryptoContextId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShPortableCryptoContexts_TenantId_OriginalBindingId",
                table: "ShPortableCryptoContexts",
                columns: new[] { "TenantId", "OriginalBindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedFiles_TenantId_KnowledgeId",
                table: "ShSealedFiles",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedKnowledge_TenantId_KnowledgeId",
                table: "ShSealedKnowledge",
                columns: new[] { "TenantId", "KnowledgeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId",
                table: "ShSealedKnowledge",
                columns: new[] { "TenantId", "OriginalItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedVersions_TenantId_KnowledgeId_VersionNumber",
                table: "ShSealedVersions",
                columns: new[] { "TenantId", "KnowledgeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShVaultCryptoContextLinks_TenantId_DestinationVaultId_Porta~",
                table: "ShVaultCryptoContextLinks",
                columns: new[] { "TenantId", "DestinationVaultId", "PortableCryptoContextId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShCryptoKeyIdentities");

            migrationBuilder.DropTable(
                name: "ShPortableCryptoContexts");

            migrationBuilder.DropTable(
                name: "ShSealedFiles");

            migrationBuilder.DropTable(
                name: "ShSealedKnowledge");

            migrationBuilder.DropTable(
                name: "ShSealedVersions");

            migrationBuilder.DropTable(
                name: "ShVaultCryptoContextLinks");
        }
    }
}
