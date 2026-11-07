using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedPlainFileMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedFieldsJson",
                table: "FileRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalFileId",
                table: "FileRecords",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedFieldsJson",
                table: "FileRecords");

            migrationBuilder.DropColumn(
                name: "OriginalFileId",
                table: "FileRecords");
        }
    }
}
