using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrentProtectedKnowledgeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedFieldsJson",
                table: "KnowledgeItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalItemId",
                table: "KnowledgeItems",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedFieldsJson",
                table: "KnowledgeItems");

            migrationBuilder.DropColumn(
                name: "OriginalItemId",
                table: "KnowledgeItems");
        }
    }
}
