using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistCommittedArchiveReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommitReceiptJson",
                table: "ShArchiveImportJournals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportOptionsJson",
                table: "ShArchiveImportJournals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitReceiptJson",
                table: "ShArchiveImportJournals");

            migrationBuilder.DropColumn(
                name: "ImportOptionsJson",
                table: "ShArchiveImportJournals");
        }
    }
}
