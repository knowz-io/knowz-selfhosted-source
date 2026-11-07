using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSealedGraphLogicalDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShSealedVersions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShSealedKnowledge",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShSealedFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShSealedComments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShSealedAttachments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "ShPayloadKeyReferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShSealedVersions");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShSealedKnowledge");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShSealedFiles");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShSealedComments");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShSealedAttachments");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "ShPayloadKeyReferences");
        }
    }
}
