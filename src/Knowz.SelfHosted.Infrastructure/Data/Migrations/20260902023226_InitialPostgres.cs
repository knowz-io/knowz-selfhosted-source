using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knowz.SelfHosted.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrichmentActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentActivityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrichmentOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiProcessingAttempts = table.Column<int>(type: "integer", nullable: false),
                    StartedProcessingAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    BlobUri = table.Column<string>(type: "text", nullable: true),
                    TranscriptionText = table.Column<string>(type: "text", nullable: true),
                    ExtractedText = table.Column<string>(type: "text", nullable: true),
                    VisionDescription = table.Column<string>(type: "text", nullable: true),
                    VisionTagsJson = table.Column<string>(type: "text", nullable: true),
                    VisionObjectsJson = table.Column<string>(type: "text", nullable: true),
                    VisionExtractedText = table.Column<string>(type: "text", nullable: true),
                    VisionAnalyzedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttachmentAIProvider = table.Column<string>(type: "text", nullable: true),
                    LayoutDataJson = table.Column<string>(type: "text", nullable: true),
                    TextExtractedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TextExtractionStatus = table.Column<int>(type: "integer", nullable: false),
                    TextExtractionError = table.Column<string>(type: "text", nullable: true),
                    BlobMigrationPending = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastSyncCommitSha = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FilePatterns = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true),
                    TrackCommitHistory = table.Column<bool>(type: "boolean", nullable: false),
                    CommitHistoryDepth = table.Column<int>(type: "integer", nullable: true),
                    LastCommitHistorySyncSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Persons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformApiUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ApiKeyProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ApiKeyLast4 = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RemoteTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastTestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTestStatus = table.Column<int>(type: "integer", nullable: false),
                    LastTestError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultSyncLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    BytesTransferred = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortableArchives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    OriginalId = table.Column<Guid>(type: "uuid", nullable: false),
                    JsonData = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortableArchives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateText = table.Column<string>(type: "text", nullable: false),
                    MergeStrategy = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSystemSeeded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EncryptedValue = table.Column<string>(type: "text", nullable: true),
                    IsSecret = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresRestart = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Topics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Topics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    VaultType = table.Column<int>(type: "integer", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ParentVaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vaults_Vaults_ParentVaultId",
                        column: x => x.ParentVaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VaultSyncLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlatformApiUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApiKeyEncrypted = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastSyncCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPullCursor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPushCursor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<int>(type: "integer", nullable: false),
                    LastSyncError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSyncLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OAuthProvider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OAuthSubjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OAuthEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    BriefSummary = table.Column<string>(type: "text", nullable: true),
                    SummaryRefinementGuidance = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: true),
                    FilePath = table.Column<string>(type: "text", nullable: true),
                    IsIndexed = table.Column<bool>(type: "boolean", nullable: false),
                    IndexedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeItems_Topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "Topics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VaultAncestors",
                columns: table => new
                {
                    AncestorVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    DescendantVaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultAncestors", x => new { x.AncestorVaultId, x.DescendantVaultId });
                    table.ForeignKey(
                        name: "FK_VaultAncestors_Vaults_AncestorVaultId",
                        column: x => x.AncestorVaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VaultAncestors_Vaults_DescendantVaultId",
                        column: x => x.DescendantVaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VaultPersons",
                columns: table => new
                {
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultPersons", x => new { x.VaultId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_VaultPersons_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VaultPersons_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncTombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultSyncLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Propagated = table.Column<bool>(type: "boolean", nullable: false),
                    PropagatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncTombstones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncTombstones_VaultSyncLinks_VaultSyncLinkId",
                        column: x => x.VaultSyncLinkId,
                        principalTable: "VaultSyncLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanCreateVaults = table.Column<bool>(type: "boolean", nullable: false),
                    HasAllVaultsAccess = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPermissions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeZonePreference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTenantMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenantMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserTenantMemberships_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserTenantMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserVaultAccess",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanRead = table.Column<bool>(type: "boolean", nullable: false),
                    CanWrite = table.Column<bool>(type: "boolean", nullable: false),
                    CanDelete = table.Column<bool>(type: "boolean", nullable: false),
                    CanManage = table.Column<bool>(type: "boolean", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserVaultAccess", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserVaultAccess_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserVaultAccess_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorName = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    IsAnswer = table.Column<bool>(type: "boolean", nullable: false),
                    Sentiment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_Comments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Comments_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    EmbeddingVectorJson = table.Column<string>(type: "text", nullable: true),
                    ContextSummary = table.Column<string>(type: "text", nullable: true),
                    IsContextualEmbedding = table.Column<bool>(type: "boolean", nullable: false),
                    EmbeddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentChunks_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeEvents",
                columns: table => new
                {
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeEvents", x => new { x.KnowledgeId, x.EventId });
                    table.ForeignKey(
                        name: "FK_KnowledgeEvents_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeEvents_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeLocations",
                columns: table => new
                {
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeLocations", x => new { x.KnowledgeId, x.LocationId });
                    table.ForeignKey(
                        name: "FK_KnowledgeLocations_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeLocations_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgePersons",
                columns: table => new
                {
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipContext = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "text", nullable: true),
                    Mentions = table.Column<int>(type: "integer", nullable: false),
                    ConfidenceScore = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgePersons", x => new { x.KnowledgeId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_KnowledgePersons_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgePersons_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Weight = table.Column<double>(type: "double precision", nullable: false),
                    IsBidirectional = table.Column<bool>(type: "boolean", nullable: false),
                    IsAutoDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Metadata = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PlatformData = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeRelationships_KnowledgeItems_SourceKnowledgeId",
                        column: x => x.SourceKnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeRelationships_KnowledgeItems_TargetKnowledgeId",
                        column: x => x.TargetKnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeTags",
                columns: table => new
                {
                    KnowledgeItemsId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagsId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeTags", x => new { x.KnowledgeItemsId, x.TagsId });
                    table.ForeignKey(
                        name: "FK_KnowledgeTags_KnowledgeItems_KnowledgeItemsId",
                        column: x => x.KnowledgeItemsId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeTags_Tags_TagsId",
                        column: x => x.TagsId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeVaults",
                columns: table => new
                {
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeVaults", x => new { x.KnowledgeId, x.VaultId });
                    table.ForeignKey(
                        name: "FK_KnowledgeVaults_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeVaults_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileAttachments_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FileAttachments_FileRecords_FileRecordId",
                        column: x => x.FileRecordId,
                        principalTable: "FileRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileAttachments_KnowledgeItems_KnowledgeId",
                        column: x => x.KnowledgeId,
                        principalTable: "KnowledgeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "EntityId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_KnowledgeId",
                table: "Comments",
                column: "KnowledgeId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentCommentId",
                table: "Comments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId",
                table: "Comments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId_KnowledgeId",
                table: "Comments",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_KnowledgeId_ContentHash",
                table: "ContentChunks",
                columns: new[] { "KnowledgeId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_KnowledgeId_Position",
                table: "ContentChunks",
                columns: new[] { "KnowledgeId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_TenantId_KnowledgeId",
                table: "ContentChunks",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentActivityLogs_TenantId_KnowledgeId",
                table: "EnrichmentActivityLogs",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentActivityLogs_TenantId_StartedAt",
                table: "EnrichmentActivityLogs",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentOutbox_KnowledgeId",
                table: "EnrichmentOutbox",
                column: "KnowledgeId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentOutbox_Status_NextRetryAt",
                table: "EnrichmentOutbox",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentOutbox_TenantId_Status",
                table: "EnrichmentOutbox",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId",
                table: "Events",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_CommentId",
                table: "FileAttachments",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_FileRecordId",
                table: "FileAttachments",
                column: "FileRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_KnowledgeId",
                table: "FileAttachments",
                column: "KnowledgeId");

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_TenantId",
                table: "FileAttachments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FileRecords_TenantId",
                table: "FileRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositories_TenantId",
                table: "GitRepositories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositories_VaultId",
                table: "GitRepositories",
                column: "VaultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_TenantId",
                table: "InboxItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_TenantId_CreatedByUserId_CreatedAt",
                table: "InboxItems",
                columns: new[] { "TenantId", "CreatedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEvents_EventId",
                table: "KnowledgeEvents",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId",
                table: "KnowledgeItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId_CreatedAt",
                table: "KnowledgeItems",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId_CreatedByUserId",
                table: "KnowledgeItems",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId_FilePath",
                table: "KnowledgeItems",
                columns: new[] { "TenantId", "FilePath" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId_Title",
                table: "KnowledgeItems",
                columns: new[] { "TenantId", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TenantId_Type",
                table: "KnowledgeItems",
                columns: new[] { "TenantId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_TopicId",
                table: "KnowledgeItems",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeLocations_LocationId",
                table: "KnowledgeLocations",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgePersons_PersonId",
                table: "KnowledgePersons",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_Source_Type",
                table: "KnowledgeRelationships",
                columns: new[] { "SourceKnowledgeId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_Target_Type",
                table: "KnowledgeRelationships",
                columns: new[] { "TargetKnowledgeId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_Tenant_Source_Target",
                table: "KnowledgeRelationships",
                columns: new[] { "TenantId", "SourceKnowledgeId", "TargetKnowledgeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelationships_TenantId",
                table: "KnowledgeRelationships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeTags_TagsId",
                table: "KnowledgeTags",
                column: "TagsId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeVaults_TenantId",
                table: "KnowledgeVaults",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeVaults_VaultId",
                table: "KnowledgeVaults",
                column: "VaultId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeVersions_KnowledgeId_VersionNumber",
                table: "KnowledgeVersions",
                columns: new[] { "KnowledgeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeVersions_TenantId_KnowledgeId",
                table: "KnowledgeVersions",
                columns: new[] { "TenantId", "KnowledgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId",
                table: "Locations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_TenantId",
                table: "Persons",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_TenantId",
                table: "PlatformConnections",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSyncRuns_TenantId_InProgress",
                table: "PlatformSyncRuns",
                column: "TenantId",
                filter: "\"CompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSyncRuns_TenantId_StartedAt",
                table: "PlatformSyncRuns",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSyncRuns_TenantId_Status",
                table: "PlatformSyncRuns",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSyncRuns_VaultSyncLinkId",
                table: "PlatformSyncRuns",
                column: "VaultSyncLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_PortableArchives_TenantId_EntityType",
                table: "PortableArchives",
                columns: new[] { "TenantId", "EntityType" });

            migrationBuilder.CreateIndex(
                name: "IX_PortableArchives_TenantId_OriginalId",
                table: "PortableArchives",
                columns: new[] { "TenantId", "OriginalId" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplates_Key_Scope_Tenant_User",
                table: "PromptTemplates",
                columns: new[] { "PromptKey", "Scope", "TenantId", "UserId" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplates_TenantId_PromptKey",
                table: "PromptTemplates",
                columns: new[] { "TenantId", "PromptKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplates_UserId_PromptKey",
                table: "PromptTemplates",
                columns: new[] { "UserId", "PromptKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncTombstones_VaultSyncLinkId_EntityType_LocalEntityId",
                table: "SyncTombstones",
                columns: new[] { "VaultSyncLinkId", "EntityType", "LocalEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncTombstones_VaultSyncLinkId_Propagated",
                table: "SyncTombstones",
                columns: new[] { "VaultSyncLinkId", "Propagated" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemConfigurations_Category",
                table: "SystemConfigurations",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_SystemConfigurations_Category_Key",
                table: "SystemConfigurations",
                columns: new[] { "Category", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_Name",
                table: "Tags",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Topics_TenantId_Name",
                table: "Topics",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissions_TenantId",
                table: "UserPermissions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissions_UserId",
                table: "UserPermissions",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_TenantId",
                table: "UserPreferences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId",
                table: "UserPreferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ApiKey",
                table: "Users",
                column: "ApiKey",
                unique: true,
                filter: "\"ApiKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true,
                filter: "\"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Username",
                table: "Users",
                columns: new[] { "TenantId", "Username" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantMemberships_TenantId",
                table: "UserTenantMemberships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantMemberships_UserId",
                table: "UserTenantMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantMemberships_UserId_TenantId",
                table: "UserTenantMemberships",
                columns: new[] { "UserId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserVaultAccess_TenantId",
                table: "UserVaultAccess",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserVaultAccess_UserId",
                table: "UserVaultAccess",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserVaultAccess_UserId_VaultId",
                table: "UserVaultAccess",
                columns: new[] { "UserId", "VaultId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserVaultAccess_VaultId",
                table: "UserVaultAccess",
                column: "VaultId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultAncestors_DescendantVaultId",
                table: "VaultAncestors",
                column: "DescendantVaultId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultPersons_PersonId",
                table: "VaultPersons",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_ParentVaultId",
                table: "Vaults",
                column: "ParentVaultId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_TenantId",
                table: "Vaults",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_TenantId_Name",
                table: "Vaults",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultSyncLinks_LocalVaultId",
                table: "VaultSyncLinks",
                column: "LocalVaultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultSyncLinks_PlatformConnectionId",
                table: "VaultSyncLinks",
                column: "PlatformConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ContentChunks");

            migrationBuilder.DropTable(
                name: "EnrichmentActivityLogs");

            migrationBuilder.DropTable(
                name: "EnrichmentOutbox");

            migrationBuilder.DropTable(
                name: "FileAttachments");

            migrationBuilder.DropTable(
                name: "GitRepositories");

            migrationBuilder.DropTable(
                name: "InboxItems");

            migrationBuilder.DropTable(
                name: "KnowledgeEvents");

            migrationBuilder.DropTable(
                name: "KnowledgeLocations");

            migrationBuilder.DropTable(
                name: "KnowledgePersons");

            migrationBuilder.DropTable(
                name: "KnowledgeRelationships");

            migrationBuilder.DropTable(
                name: "KnowledgeTags");

            migrationBuilder.DropTable(
                name: "KnowledgeVaults");

            migrationBuilder.DropTable(
                name: "KnowledgeVersions");

            migrationBuilder.DropTable(
                name: "PlatformConnections");

            migrationBuilder.DropTable(
                name: "PlatformSyncRuns");

            migrationBuilder.DropTable(
                name: "PortableArchives");

            migrationBuilder.DropTable(
                name: "PromptTemplates");

            migrationBuilder.DropTable(
                name: "SyncTombstones");

            migrationBuilder.DropTable(
                name: "SystemConfigurations");

            migrationBuilder.DropTable(
                name: "UserPermissions");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "UserTenantMemberships");

            migrationBuilder.DropTable(
                name: "UserVaultAccess");

            migrationBuilder.DropTable(
                name: "VaultAncestors");

            migrationBuilder.DropTable(
                name: "VaultPersons");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "FileRecords");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "VaultSyncLinks");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Persons");

            migrationBuilder.DropTable(
                name: "Vaults");

            migrationBuilder.DropTable(
                name: "KnowledgeItems");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Topics");
        }
    }
}
