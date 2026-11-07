using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowLogicalCryptoCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId_LineageScope",
                table: "ShSealedKnowledge");

            migrationBuilder.DropIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId_LineageScope",
                table: "ShSealedComments");

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId_LineageScope",
                table: "ShSealedKnowledge",
                columns: new[] { "TenantId", "OriginalItemId", "LineageScope" });

            migrationBuilder.CreateIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId_LineageScope",
                table: "ShSealedComments",
                columns: new[] { "TenantId", "OriginalCommentId", "LineageScope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShSealedKnowledge_TenantId_OriginalItemId_LineageScope",
                table: "ShSealedKnowledge");

            migrationBuilder.DropIndex(
                name: "IX_ShSealedComments_TenantId_OriginalCommentId_LineageScope",
                table: "ShSealedComments");

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
        }
    }
}
