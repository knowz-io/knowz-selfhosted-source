using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInactiveRecoveryAndPeerReceiptResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalSourceBaseUrl",
                table: "ShArchivePeerBindings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingLocalSourceBaseUrl",
                table: "ShArchivePeerBindings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingRemoteChallengeJson",
                table: "ShArchivePeerBindings",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationChallengeSha256",
                table: "ShArchivePeerBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RemoteBindingId",
                table: "ShArchivePeerBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteBindingRevision",
                table: "ShArchivePeerBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortableRecoveryState",
                table: "KnowledgeItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShInactiveRecoveryRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecoveryState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PoliciesJson = table.Column<string>(type: "text", nullable: false),
                    DependencyJson = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShInactiveRecoveryRecord", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShInactiveRecoveryRecord");

            migrationBuilder.DropColumn(
                name: "LocalSourceBaseUrl",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "PendingLocalSourceBaseUrl",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "PendingRemoteChallengeJson",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "RegistrationChallengeSha256",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "RemoteBindingId",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "RemoteBindingRevision",
                table: "ShArchivePeerBindings");

            migrationBuilder.DropColumn(
                name: "PortableRecoveryState",
                table: "KnowledgeItems");
        }
    }
}
