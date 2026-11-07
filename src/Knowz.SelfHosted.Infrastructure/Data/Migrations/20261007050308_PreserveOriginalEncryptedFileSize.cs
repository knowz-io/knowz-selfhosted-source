using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreserveOriginalEncryptedFileSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OriginalSizeBytes",
                table: "ShSealedFiles",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalSizeBytes",
                table: "ShSealedFiles");
        }
    }
}
