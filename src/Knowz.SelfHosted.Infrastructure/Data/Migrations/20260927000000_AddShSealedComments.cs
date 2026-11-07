using System;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    [DbContext(typeof(SelfHostedDbContext))]
    [Migration("20260927000000_AddShSealedComments")]
    public partial class AddShSealedComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShSealedComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalCommentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalKnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    EnvelopeVersion = table.Column<int>(type: "integer", nullable: true),
                    EncryptedDek = table.Column<string>(type: "text", nullable: true),
                    EncryptedDekNonce = table.Column<string>(type: "text", nullable: true),
                    EphemeralPublicKey = table.Column<string>(type: "text", nullable: true),
                    ContentNonce = table.Column<string>(type: "text", nullable: true),
                    EncryptionKeyRef = table.Column<string>(type: "text", nullable: true),
                    EncryptedFieldsJson = table.Column<string>(type: "text", nullable: true),
                    CryptoBindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlgorithmProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShSealedComments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedComments_TenantId_KnowledgeId",
                table: "ShSealedComments",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId",
                table: "ShSealedComments",
                columns: new[] { "TenantId", "OriginalCommentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShSealedComments");
        }
    }
}
