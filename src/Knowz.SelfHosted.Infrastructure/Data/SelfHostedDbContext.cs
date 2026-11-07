using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Data;

public partial class SelfHostedDbContext : DbContext
{
    private readonly ITenantProvider? _tenantProvider;
    private readonly IPortableActorProvider? _portableActor;
    public Guid? PortableActorId => _portableActor?.ActorId;
    // API-key authentication resolves this context before assigning the principal.
    // Evaluate the existing trusted provider when a query runs, after authentication.
    private Guid _tenantId => _tenantProvider?.TenantId ?? Guid.Parse("00000000-0000-0000-0000-000000000001");

    public SelfHostedDbContext(DbContextOptions<SelfHostedDbContext> options, ITenantProvider tenantProvider, IPortableActorProvider? portableActor = null)
        : base(options)
    {
        _tenantProvider = tenantProvider; _portableActor = portableActor;
    }

    // For migrations (design-time factory) - internal so pooling sees only one public constructor
    internal SelfHostedDbContext(DbContextOptions<SelfHostedDbContext> options)
        : base(options)
    {
    }

    public DbSet<Knowledge> KnowledgeItems => Set<Knowledge>();
    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<KnowledgeVault> KnowledgeVaults => Set<KnowledgeVault>();
    public DbSet<KnowledgePerson> KnowledgePersons => Set<KnowledgePerson>();
    public DbSet<KnowledgeLocation> KnowledgeLocations => Set<KnowledgeLocation>();
    public DbSet<KnowledgeEvent> KnowledgeEvents => Set<KnowledgeEvent>();
    public DbSet<VaultAncestor> VaultAncestors => Set<VaultAncestor>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();

    // v2 entities
    public DbSet<VaultPerson> VaultPersons => Set<VaultPerson>();
    public DbSet<KnowledgeComment> Comments => Set<KnowledgeComment>();
    public DbSet<ShCryptoContextImportAnchor> ShCryptoContextImportAnchors => Set<ShCryptoContextImportAnchor>();
    public DbSet<ShCryptoCustodyChallenge> ShCryptoCustodyChallenges => Set<ShCryptoCustodyChallenge>();
    public DbSet<ShRecoveredKeyCustodyAcceptance> ShRecoveredKeyCustodyAcceptances => Set<ShRecoveredKeyCustodyAcceptance>();
    public DbSet<ShArchivePeerBinding> ShArchivePeerBindings => Set<ShArchivePeerBinding>();
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();
    public DbSet<PortableArchive> PortableArchives => Set<PortableArchive>();

    // Knowledge relationships (tenant-scoped with query filter)
    public DbSet<KnowledgeRelationship> KnowledgeRelationships => Set<KnowledgeRelationship>();

    // Content chunks (tenant-scoped with query filter)
    public DbSet<ContentChunk> ContentChunks => Set<ContentChunk>();

    // Auth entities (admin-level, NOT tenant-scoped by query filters)
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();

    // User vault permissions (admin-level, NOT tenant-scoped by query filters)
    public DbSet<UserPermissions> UserPermissions => Set<UserPermissions>();
    public DbSet<UserVaultAccess> UserVaultAccess => Set<UserVaultAccess>();

    // User preferences — cosmetic/UX settings (timezone, etc.) per user
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    // Prompt templates (no tenant query filter — scoping done in PromptResolutionService)
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();

    // Multi-tenant user memberships (admin-level, NOT tenant-scoped by query filters)
    public DbSet<UserTenantMembership> UserTenantMemberships => Set<UserTenantMembership>();

    // Vault sync entities (no query filters — admin-level)
    public DbSet<VaultSyncLink> VaultSyncLinks => Set<VaultSyncLink>();
    public DbSet<SyncTombstone> SyncTombstones => Set<SyncTombstone>();
    public DbSet<PlatformConnection> PlatformConnections => Set<PlatformConnection>();
    public DbSet<PlatformSyncRun> PlatformSyncRuns => Set<PlatformSyncRun>();

    // Knowledge versioning and audit logging
    public DbSet<KnowledgeVersion> KnowledgeVersions => Set<KnowledgeVersion>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Git sync entities (no query filter — scoped by VaultId in service layer)
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();

    // Infrastructure entities (no query filters)
    public DbSet<EnrichmentOutboxItem> EnrichmentOutbox => Set<EnrichmentOutboxItem>();

    // Enrichment activity log — 1 row per attempt. Tenant-scoped via query filter.
    public DbSet<EnrichmentActivityLog> EnrichmentActivityLogs => Set<EnrichmentActivityLog>();

    // System configuration (no query filter — admin-level, not tenant-scoped)
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();

    // Sealed vault store. Ciphertext stays on these rows. Query-filtered by tenant.
    public DbSet<ShPortableCryptoContext> ShPortableCryptoContexts => Set<ShPortableCryptoContext>();
    public DbSet<ShVaultCryptoContextLink> ShVaultCryptoContextLinks => Set<ShVaultCryptoContextLink>();
    public DbSet<ShCryptoKeyIdentity> ShCryptoKeyIdentities => Set<ShCryptoKeyIdentity>();
    public DbSet<ShSealedKnowledge> ShSealedKnowledge => Set<ShSealedKnowledge>();
    public DbSet<ShSealedVersion> ShSealedVersions => Set<ShSealedVersion>();
    public DbSet<ShSealedFileStage> ShSealedFileStages => Set<ShSealedFileStage>();
    public DbSet<ShSealedFile> ShSealedFiles => Set<ShSealedFile>();
    public DbSet<ShSealedComment> ShSealedComments => Set<ShSealedComment>();
    public DbSet<ShSealedAttachment> ShSealedAttachments => Set<ShSealedAttachment>();
    public DbSet<ShPayloadKeyReference> ShPayloadKeyReferences => Set<ShPayloadKeyReference>();
    public DbSet<ShArchiveImportJournal> ShArchiveImportJournals => Set<ShArchiveImportJournal>();
    public DbSet<ShArchiveSigningIdentity> ShArchiveSigningIdentities => Set<ShArchiveSigningIdentity>();
    public DbSet<ShFileObjectReference> ShFileObjectReferences => Set<ShFileObjectReference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Global query filters: TenantId + soft delete
        modelBuilder.Entity<Knowledge>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted && (e.PortableRecoveryState == null || PortableActorId != null && e.CreatedByUserId == PortableActorId));
        modelBuilder.Entity<Knowledge>().Property(e => e.PortableRecoveryState).HasMaxLength(64);
        modelBuilder.Entity<ShInactiveRecoveryRecord>(e => { e.HasKey(x => x.Id); e.HasQueryFilter(x => x.TenantId == _tenantId && PortableActorId != null && x.OwnerUserId == PortableActorId); e.Property(x => x.RecoveryState).HasMaxLength(64); e.Property(x => x.Revision).IsConcurrencyToken(); });
        modelBuilder.Entity<Vault>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<Topic>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<Tag>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<Person>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<Location>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<Event>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<InboxItem>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<KnowledgeComment>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShCryptoContextImportAnchor>(entity => {
            entity.HasKey(x => x.Id); entity.HasQueryFilter(x => x.TenantId == _tenantId);
            entity.HasIndex(x => new { x.TenantId, x.PlanId, x.ContextId }).IsUnique();
            entity.Property(x => x.ManifestSha256).HasMaxLength(64); entity.Property(x => x.MappingSha256).HasMaxLength(64);
            entity.Property(x => x.PackageSha256).HasMaxLength(64); entity.Property(x => x.PlanSha256).HasMaxLength(64); entity.Property(x => x.SourceTrust).HasMaxLength(64);
        });
        modelBuilder.Entity<ShCryptoCustodyChallenge>(entity => {
            entity.HasKey(x => x.Id); entity.HasQueryFilter(x => x.TenantId == _tenantId);
            entity.HasIndex(x => new { x.TenantId, x.ActorId, x.ExpiresAt });
            entity.Property(x => x.Status).HasMaxLength(32).IsConcurrencyToken(); entity.Property(x => x.Fingerprint).HasMaxLength(64);
            entity.Property(x => x.KeyStateSha256).HasMaxLength(64); entity.Property(x => x.PlanSha256).HasMaxLength(64); entity.Property(x => x.ExpectedProofSha256).HasMaxLength(64);
        });
        modelBuilder.Entity<ShRecoveredKeyCustodyAcceptance>(entity => {
            entity.HasKey(x => x.Id); entity.HasQueryFilter(x => x.TenantId == _tenantId);
            entity.HasIndex(x => new { x.TenantId, x.ContextId, x.KeyId }).IsUnique();
            entity.Property(x => x.Fingerprint).HasMaxLength(64); entity.Property(x => x.KeyStateSha256).HasMaxLength(64); entity.Property(x => x.SourceTrust).HasMaxLength(64);
        });
        modelBuilder.Entity<ShArchivePeerBinding>(entity => {
            entity.Property(x => x.ParentBindingSha256).HasMaxLength(64);
            entity.Property(x => x.RegistrationChallengeSha256).HasMaxLength(64); entity.Property(x => x.PendingRemoteChallengeJson).HasMaxLength(8192); entity.Property(x => x.PendingLocalSourceBaseUrl).HasMaxLength(2048);
            entity.Property(x => x.RemoteBindingRevision).HasMaxLength(64); entity.Property(x => x.LocalSourceBaseUrl).HasMaxLength(2048);
            entity.HasKey(x => x.Id); entity.HasQueryFilter(x => x.TenantId == _tenantId);
            entity.Property(x => x.SourceBaseUrl).HasMaxLength(2048).IsRequired(); entity.Property(x => x.BindingSlotSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SignerFingerprint).HasMaxLength(64).IsRequired(); entity.Property(x => x.SignerPublicKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.CredentialIdentitySha256).HasMaxLength(64).IsRequired(); entity.Property(x => x.TrustProvenance).HasMaxLength(64).IsRequired();
            entity.Property(x => x.BindingRevision).HasMaxLength(64).IsRequired().IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.BindingSlotSha256 }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.DestinationVaultId });
        });
        modelBuilder.Entity<FileRecord>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ContentChunk>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<KnowledgeVersion>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<AuditLog>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<KnowledgeRelationship>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShPortableCryptoContext>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShVaultCryptoContextLink>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShCryptoKeyIdentity>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShSealedKnowledge>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShSealedVersion>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShSealedFileStage>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShSealedFile>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShSealedComment>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShSealedAttachment>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShPayloadKeyReference>().HasQueryFilter(e => e.TenantId == _tenantId && !e.IsDeleted);
        modelBuilder.Entity<ShArchiveImportJournal>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShArchiveSigningIdentity>().HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<ShFileObjectReference>().HasQueryFilter(e => e.TenantId == _tenantId);

        ConfigureInactiveRecoveryFilters(modelBuilder);

        // Junction table composite keys
        modelBuilder.Entity<KnowledgeVault>().HasKey(kv => new { kv.KnowledgeId, kv.VaultId });
        modelBuilder.Entity<KnowledgePerson>().HasKey(kp => new { kp.KnowledgeId, kp.PersonId });
        modelBuilder.Entity<KnowledgeLocation>().HasKey(kl => new { kl.KnowledgeId, kl.LocationId });
        modelBuilder.Entity<KnowledgeEvent>().HasKey(ke => new { ke.KnowledgeId, ke.EventId });
        modelBuilder.Entity<VaultAncestor>().HasKey(va => new { va.AncestorVaultId, va.DescendantVaultId });
        modelBuilder.Entity<VaultPerson>().HasKey(vp => new { vp.VaultId, vp.PersonId });

        // Tag <-> Knowledge many-to-many (EF Core implicit join)
        modelBuilder.Entity<Knowledge>()
            .HasMany(k => k.Tags)
            .WithMany(t => t.KnowledgeItems)
            .UsingEntity("KnowledgeTags");

        // Vault hierarchy self-reference
        modelBuilder.Entity<Vault>()
            .HasOne(v => v.ParentVault)
            .WithMany(v => v.Children)
            .HasForeignKey(v => v.ParentVaultId)
            .OnDelete(DeleteBehavior.Restrict);

        // VaultAncestor closure table relationships
        modelBuilder.Entity<VaultAncestor>()
            .HasOne(va => va.AncestorVault)
            .WithMany(v => v.Descendants)
            .HasForeignKey(va => va.AncestorVaultId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<VaultAncestor>()
            .HasOne(va => va.DescendantVault)
            .WithMany(v => v.Ancestors)
            .HasForeignKey(va => va.DescendantVaultId)
            .OnDelete(DeleteBehavior.Restrict);

        // VaultPerson relationships
        modelBuilder.Entity<VaultPerson>()
            .HasOne(vp => vp.Vault)
            .WithMany(v => v.VaultPersons)
            .HasForeignKey(vp => vp.VaultId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultPerson>()
            .HasOne(vp => vp.Person)
            .WithMany(p => p.VaultPersons)
            .HasForeignKey(vp => vp.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        // KnowledgeComment hierarchy
        modelBuilder.Entity<KnowledgeComment>()
            .HasOne(c => c.Knowledge)
            .WithMany(k => k.Comments)
            .HasForeignKey(c => c.KnowledgeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<KnowledgeComment>()
            .HasOne(c => c.ParentComment)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentCommentId)
            .OnDelete(DeleteBehavior.Restrict);

        // FileAttachment relationships
        modelBuilder.Entity<FileAttachment>()
            .HasOne(fa => fa.FileRecord)
            .WithMany(fr => fr.Attachments)
            .HasForeignKey(fa => fa.FileRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FileAttachment>()
            .HasOne(fa => fa.Knowledge)
            .WithMany()
            .HasForeignKey(fa => fa.KnowledgeId)
            .OnDelete(DeleteBehavior.SetNull);

        // CommentId FK with NoAction - SQL Server doesn't allow multiple cascade paths
        // (Knowledge → FileAttachments via KnowledgeId vs Knowledge → Comments → FileAttachments via CommentId)
        // Comment deletion must handle file attachments at application level
        modelBuilder.Entity<FileAttachment>()
            .HasOne(fa => fa.Comment)
            .WithMany()
            .HasForeignKey(fa => fa.CommentId)
            .OnDelete(DeleteBehavior.NoAction);

        // PortableArchive (no query filter — just TenantId scoping in queries)
        modelBuilder.Entity<PortableArchive>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.TenantId, a.EntityType });
            entity.HasIndex(a => new { a.TenantId, a.OriginalId });
        });

        // --- Tenant entity configuration (NO query filter) ---
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.Slug).IsUnique();
            entity.HasIndex(t => t.Name);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(200);
            entity.Property(t => t.Slug).IsRequired().HasMaxLength(100);
            entity.Property(t => t.Description).HasMaxLength(1000);
        });

        // --- User entity configuration (NO query filter) ---
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.ApiKey).IsUnique().HasFilter("\"ApiKey\" IS NOT NULL");
            entity.HasIndex(u => u.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
            entity.HasIndex(u => u.TenantId);
            entity.HasIndex(u => new { u.TenantId, u.Username });
            entity.Property(u => u.Username).IsRequired().HasMaxLength(100);
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.ApiKey).HasMaxLength(100);
            entity.Property(u => u.Email).HasMaxLength(255);
            entity.Property(u => u.DisplayName).HasMaxLength(200);
            entity.Property(u => u.OAuthProvider).HasMaxLength(50);
            entity.Property(u => u.OAuthSubjectId).HasMaxLength(255);
            entity.Property(u => u.OAuthEmail).HasMaxLength(255);

            entity.HasOne(u => u.Tenant)
                .WithMany(t => t.Users)
                .HasForeignKey(u => u.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- UserPermissions entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<UserPermissions>(entity =>
        {
            entity.HasKey(up => up.Id);
            entity.HasIndex(up => up.UserId).IsUnique(); // One permissions record per user
            entity.HasIndex(up => up.TenantId);

            entity.HasOne(up => up.User)
                .WithOne()
                .HasForeignKey<UserPermissions>(up => up.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- UserPreference entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.HasKey(up => up.Id);
            entity.HasIndex(up => up.UserId).IsUnique(); // One preference record per user
            entity.HasIndex(up => up.TenantId);

            entity.Property(up => up.TimeZonePreference).HasMaxLength(100);

            entity.HasOne(up => up.User)
                .WithOne()
                .HasForeignKey<UserPreference>(up => up.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- UserVaultAccess entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<UserVaultAccess>(entity =>
        {
            entity.HasKey(uva => uva.Id);
            entity.HasIndex(uva => new { uva.UserId, uva.VaultId }).IsUnique(); // One access record per user+vault
            entity.HasIndex(uva => uva.UserId);
            entity.HasIndex(uva => uva.VaultId);
            entity.HasIndex(uva => uva.TenantId);

            entity.HasOne(uva => uva.User)
                .WithMany()
                .HasForeignKey(uva => uva.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(uva => uva.Vault)
                .WithMany()
                .HasForeignKey(uva => uva.VaultId)
                .OnDelete(DeleteBehavior.NoAction); // Avoid multiple cascade paths
        });

        // --- VaultSyncLink entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<VaultSyncLink>(entity =>
        {
            entity.Property(x => x.SecurityRevision).HasMaxLength(64).IsConcurrencyToken();
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LocalVaultId).IsUnique();
            entity.HasIndex(e => e.PlatformConnectionId);
#pragma warning disable CS0618 // Obsolete properties retained during data-copy migration window
            entity.Property(e => e.PlatformApiUrl).IsRequired(false).HasMaxLength(500);
            entity.Property(e => e.ApiKeyEncrypted).IsRequired(false).HasMaxLength(1000);
#pragma warning restore CS0618
            entity.Property(e => e.LastSyncError).HasMaxLength(2000);
        });

        // --- PlatformConnection entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<PlatformConnection>(entity =>
        {
            entity.Property(x => x.SecurityRevision).HasMaxLength(64).IsConcurrencyToken();
            entity.HasKey(e => e.Id);
            // ADR_G6 (#1043): one-of-many per tenant. URL is normalized (Trim + TrimEnd('/'))
            // by PlatformConnectionService before write, so the composite key is meaningful.
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.PlatformApiUrl }).IsUnique();
            entity.Property(e => e.PlatformApiUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ApiKeyProtected).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.ApiKeyLast4).HasMaxLength(8);
            entity.Property(e => e.DisplayName).HasMaxLength(100);
            entity.Property(e => e.LastTestError).HasMaxLength(2000);
            entity.Property(e => e.LastTestStatus).HasConversion<int>();
        });

        // --- SyncTombstone entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<SyncTombstone>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.VaultSyncLinkId, e.Propagated });
            entity.HasIndex(e => new { e.VaultSyncLinkId, e.EntityType, e.LocalEntityId });
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(100);

            entity.HasOne(e => e.VaultSyncLink)
                .WithMany()
                .HasForeignKey(e => e.VaultSyncLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- PlatformSyncRun entity configuration (NO query filter — admin-level audit log) ---
        modelBuilder.Entity<PlatformSyncRun>(entity =>
        {
            entity.HasKey(e => e.Id);

            // History page query: WHERE TenantId = @t ORDER BY StartedAt DESC
            entity.HasIndex(e => new { e.TenantId, e.StartedAt })
                .HasDatabaseName("IX_PlatformSyncRuns_TenantId_StartedAt");

            // Concurrency + rate limiter path: WHERE TenantId = @t AND Status = InProgress
            entity.HasIndex(e => new { e.TenantId, e.Status })
                .HasDatabaseName("IX_PlatformSyncRuns_TenantId_Status");

            // Filtered index for fast in-progress lookups: WHERE CompletedAt IS NULL.
            // Matches the CountInProgressAsync / concurrency-guard read path without forcing a
            // full scan when the table grows. Keyed on TenantId so concurrent tenants don't collide.
            entity.HasIndex(e => e.TenantId)
                .HasDatabaseName("IX_PlatformSyncRuns_TenantId_InProgress")
                .HasFilter("\"CompletedAt\" IS NULL");

            // Per-link drill-down
            entity.HasIndex(e => e.VaultSyncLinkId)
                .HasDatabaseName("IX_PlatformSyncRuns_VaultSyncLinkId");

            entity.Property(e => e.UserEmail).HasMaxLength(255);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
            entity.Property(e => e.Operation).HasConversion<int>();
            entity.Property(e => e.Direction).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
        });

        // EnrichmentOutbox (no query filter — infrastructure entity)
        modelBuilder.Entity<EnrichmentOutboxItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KnowledgeId);
            entity.HasIndex(e => new { e.Status, e.NextRetryAt });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
        });

        // EnrichmentActivityLog — tenant-scoped, one row per attempt
        modelBuilder.Entity<EnrichmentActivityLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.KnowledgeId });
            entity.HasIndex(e => new { e.TenantId, e.StartedAt });
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.HasQueryFilter(e => e.TenantId == _tenantId);
        });

        // --- PromptTemplate entity configuration (NO query filter — scoping in service) ---
        modelBuilder.Entity<PromptTemplate>(entity =>
        {
            entity.HasKey(pt => pt.Id);
            entity.Property(pt => pt.PromptKey).IsRequired().HasMaxLength(100);
            entity.Property(pt => pt.TemplateText).IsRequired();
            entity.Property(pt => pt.Description).HasMaxLength(500);
            entity.Property(pt => pt.LastModifiedBy).HasMaxLength(100);
            entity.HasIndex(pt => new { pt.PromptKey, pt.Scope, pt.TenantId, pt.UserId })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL AND \"UserId\" IS NOT NULL")
                .HasDatabaseName("IX_PromptTemplates_Key_Scope_Tenant_User");
            entity.HasIndex(pt => new { pt.TenantId, pt.PromptKey })
                .HasDatabaseName("IX_PromptTemplates_TenantId_PromptKey");
            entity.HasIndex(pt => new { pt.UserId, pt.PromptKey })
                .HasDatabaseName("IX_PromptTemplates_UserId_PromptKey");
        });

        // --- UserTenantMembership entity configuration (NO query filter — admin-level) ---
        modelBuilder.Entity<UserTenantMembership>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.UserId, m.TenantId }).IsUnique();
            entity.HasIndex(m => m.UserId);
            entity.HasIndex(m => m.TenantId);

            entity.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Tenant)
                .WithMany()
                .HasForeignKey(m => m.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // --- SystemConfiguration entity configuration (NO query filter) ---
        modelBuilder.Entity<SystemConfiguration>(entity =>
        {
            entity.HasKey(sc => sc.Id);
            entity.HasIndex(sc => new { sc.Category, sc.Key }).IsUnique();
            entity.HasIndex(sc => sc.Category);
            entity.Property(sc => sc.Category).IsRequired().HasMaxLength(100);
            entity.Property(sc => sc.Key).IsRequired().HasMaxLength(200);
            entity.Property(sc => sc.Description).HasMaxLength(500);
            entity.Property(sc => sc.LastModifiedBy).HasMaxLength(100);
            var rowVersion = entity.Property(sc => sc.RowVersion).IsConcurrencyToken();
            if (Database.IsNpgsql())
            {
                // PostgreSQL has no SQL Server-style byte[] rowversion. Keep the
                // client value so inserts include a non-null bytea payload.
                rowVersion.ValueGeneratedNever();
            }
            else
            {
                rowVersion.IsRowVersion();
            }
        });

        // Indexes for common queries
        modelBuilder.Entity<Knowledge>().HasIndex(k => k.TenantId);
        modelBuilder.Entity<Knowledge>().HasIndex(k => new { k.TenantId, k.Type });
        modelBuilder.Entity<Knowledge>().HasIndex(k => new { k.TenantId, k.CreatedAt });
        modelBuilder.Entity<Knowledge>().HasIndex(k => new { k.TenantId, k.Title });
        modelBuilder.Entity<Knowledge>().HasIndex(k => new { k.TenantId, k.FilePath });
        modelBuilder.Entity<Knowledge>().HasIndex(k => new { k.TenantId, k.CreatedByUserId });
        modelBuilder.Entity<Vault>().HasIndex(v => v.TenantId);
        modelBuilder.Entity<Vault>().HasIndex(v => new { v.TenantId, v.Name });
        modelBuilder.Entity<Topic>().HasIndex(t => new { t.TenantId, t.Name });
        modelBuilder.Entity<Tag>().HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
        modelBuilder.Entity<KnowledgeVault>().HasIndex(kv => kv.VaultId);
        modelBuilder.Entity<KnowledgeVault>().HasIndex(kv => kv.TenantId);
        modelBuilder.Entity<Event>().HasIndex(e => e.TenantId);
        modelBuilder.Entity<InboxItem>().HasIndex(i => i.TenantId);
        modelBuilder.Entity<InboxItem>()
            .HasIndex(i => new { i.TenantId, i.CreatedByUserId, i.CreatedAt })
            .HasDatabaseName("IX_InboxItems_TenantId_CreatedByUserId_CreatedAt");
        modelBuilder.Entity<Location>().HasIndex(l => l.TenantId);
        modelBuilder.Entity<Person>().HasIndex(p => p.TenantId);
        modelBuilder.Entity<KnowledgeComment>().HasIndex(c => c.TenantId);
        modelBuilder.Entity<KnowledgeComment>().HasIndex(c => new { c.TenantId, c.KnowledgeId });
        modelBuilder.Entity<FileRecord>().HasIndex(f => f.TenantId);
        modelBuilder.Entity<FileRecord>().Property(f => f.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<FileRecord>().Property(f => f.FileVersion).HasDefaultValue(1);
        modelBuilder.Entity<FileRecord>().Property(f => f.IsCurrentVersion).HasDefaultValue(true);
        modelBuilder.Entity<FileAttachment>().HasIndex(fa => fa.TenantId);
        modelBuilder.Entity<FileAttachment>().HasIndex(fa => fa.FileRecordId);
        modelBuilder.Entity<KnowledgeRelationship>().HasIndex(r => r.TenantId);

        // ContentChunk relationships and indexes
        modelBuilder.Entity<ContentChunk>()
            .HasOne(c => c.Knowledge)
            .WithMany()
            .HasForeignKey(c => c.KnowledgeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ContentChunk>()
            .HasIndex(c => new { c.TenantId, c.KnowledgeId });

        modelBuilder.Entity<ContentChunk>()
            .HasIndex(c => new { c.KnowledgeId, c.Position });

        modelBuilder.Entity<ContentChunk>()
            .HasIndex(c => new { c.KnowledgeId, c.ContentHash });

        // KnowledgeVersion entity configuration
        modelBuilder.Entity<KnowledgeVersion>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => new { v.TenantId, v.KnowledgeId });
            entity.HasIndex(v => new { v.KnowledgeId, v.VersionNumber }).IsUnique();
            entity.Property(v => v.Title).IsRequired().HasMaxLength(500);
            entity.Property(v => v.ContentType).HasMaxLength(100);
            entity.Property(v => v.ChangeDescription).HasMaxLength(1000);
        });

        // AuditLog entity configuration
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
            entity.HasIndex(a => new { a.TenantId, a.Timestamp });
            entity.HasIndex(a => new { a.EntityId, a.Timestamp });
            entity.Property(a => a.EntityType).IsRequired().HasMaxLength(100);
            entity.Property(a => a.Action).IsRequired().HasMaxLength(100);
            entity.Property(a => a.UserEmail).HasMaxLength(255);
            entity.Property(a => a.Details).HasMaxLength(4000);
        });

        // --- KnowledgeRelationship entity configuration ---
        modelBuilder.Entity<KnowledgeRelationship>(entity =>
        {
            entity.HasKey(r => r.Id);

            // Unique constraint: one relationship per source+target pair per tenant
            entity.HasIndex(r => new { r.TenantId, r.SourceKnowledgeId, r.TargetKnowledgeId })
                .IsUnique()
                .HasDatabaseName("IX_KnowledgeRelationships_Tenant_Source_Target");

            // Graph traversal indexes
            entity.HasIndex(r => new { r.SourceKnowledgeId, r.RelationshipType })
                .HasDatabaseName("IX_KnowledgeRelationships_Source_Type");
            entity.HasIndex(r => new { r.TargetKnowledgeId, r.RelationshipType })
                .HasDatabaseName("IX_KnowledgeRelationships_Target_Type");

            // Enum stored as int
            entity.Property(r => r.RelationshipType)
                .HasConversion<int>();

            entity.Property(r => r.Metadata).HasMaxLength(4000);

            // Source FK → Cascade
            entity.HasOne(r => r.SourceKnowledge)
                .WithMany(k => k.OutgoingRelationships)
                .HasForeignKey(r => r.SourceKnowledgeId)
                .OnDelete(DeleteBehavior.Cascade);

            // Target FK → NoAction (SQL Server multi-cascade limitation)
            entity.HasOne(r => r.TargetKnowledge)
                .WithMany(k => k.IncomingRelationships)
                .HasForeignKey(r => r.TargetKnowledgeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // --- GitRepository entity configuration (NO query filter — scoped by VaultId in service) ---
        modelBuilder.Entity<GitRepository>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.HasIndex(g => g.VaultId).IsUnique(); // One repo per vault
            entity.HasIndex(g => g.TenantId);
            entity.Property(g => g.RepositoryUrl).IsRequired().HasMaxLength(1000);
            entity.Property(g => g.Branch).IsRequired().HasMaxLength(200);
            entity.Property(g => g.Status).IsRequired().HasMaxLength(50);
            entity.Property(g => g.LastSyncCommitSha).HasMaxLength(100);
            entity.Property(g => g.FilePatterns).HasMaxLength(2000);
            entity.Property(g => g.ErrorMessage).HasMaxLength(2000);
            // NODE-4: commit-history ingestion fields
            entity.Property(g => g.LastCommitHistorySyncSha).HasMaxLength(40);
        });

        modelBuilder.Entity<ShSealedAttachment>(entity => { entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.FileRecordId }); });
        modelBuilder.Entity<ShPayloadKeyReference>(entity => { entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.OwnerKind, x.OwnerId, x.Role, x.FieldName }).IsUnique(); });
        modelBuilder.Entity<ShArchiveImportJournal>(entity => { entity.HasKey(x => x.PlanId); entity.Property(x => x.ActorNamespace).HasMaxLength(512); entity.Property(x => x.PeerBindingSha256).HasMaxLength(64); entity.Property(x => x.Status).IsConcurrencyToken(); entity.Property(x => x.UpdatedAt).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.Status, x.UpdatedAt }); });
        modelBuilder.Entity<ShSealedFileStage>(entity => { entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ActorId, x.IdempotencyKey }).IsUnique(); entity.Property(x => x.Status).IsConcurrencyToken(); });
        modelBuilder.Entity<ShSealedKnowledge>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<ShSealedComment>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<ShSealedFile>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<ShSealedFile>().Property(x => x.FileVersion).HasDefaultValue(1);
        modelBuilder.Entity<ShSealedFile>().Property(x => x.IsCurrentVersion).HasDefaultValue(true);
        modelBuilder.Entity<ShSealedAttachment>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<ShArchiveSigningIdentity>(entity => { entity.HasKey(x => x.TenantId); });
        modelBuilder.Entity<ShFileObjectReference>(entity => { entity.HasKey(x => x.FileRecordId); });
        modelBuilder.Entity<ShPortableCryptoContext>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.OriginalBindingId });
            entity.Property(c => c.BindingKind).IsRequired().HasMaxLength(32);
            entity.Property(c => c.AlgorithmProfile).IsRequired().HasMaxLength(64);
            entity.Property(c => c.ProvenanceStatus).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<ShVaultCryptoContextLink>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.TenantId, l.DestinationVaultId, l.PortableCryptoContextId }).IsUnique();
        });

        modelBuilder.Entity<ShCryptoKeyIdentity>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.HasIndex(k => new { k.TenantId, k.PortableCryptoContextId, k.Fingerprint, k.Generation }).IsUnique();
            entity.Property(k => k.Fingerprint).IsRequired().HasMaxLength(128);
            entity.Property(k => k.PublicKey).IsRequired();
            entity.Property(k => k.KeyType).IsRequired().HasMaxLength(32);
            entity.Property(k => k.Lifecycle).IsRequired().HasMaxLength(32);
            entity.Property(k => k.Custody).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<ShSealedKnowledge>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.TenantId, s.KnowledgeId }).IsUnique();
            entity.HasIndex(s => new { s.TenantId, s.OriginalItemId, s.LineageScope });
            entity.Property(s => s.Ciphertext).IsRequired();
            entity.Property(s => s.AlgorithmProfile).IsRequired().HasMaxLength(64);
            entity.Property(s => s.LifecycleStatus).IsRequired().HasMaxLength(32);
            entity.Property(s => s.VerifiedFingerprint).HasMaxLength(128);
        });

        modelBuilder.Entity<ShSealedVersion>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => new { v.TenantId, v.KnowledgeId, v.VersionNumber }).IsUnique();
            entity.Property(v => v.Ciphertext).IsRequired();
            entity.Property(v => v.EnvelopeMetadata).IsRequired();
        });

        modelBuilder.Entity<ShSealedFile>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.TenantId, f.KnowledgeId });
            entity.Property(f => f.ContentType).HasMaxLength(200);
        });

        modelBuilder.Entity<ShSealedComment>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.KnowledgeId });
            entity.HasIndex(c => new { c.TenantId, c.OriginalCommentId, c.LineageScope });
            entity.Property(c => c.Ciphertext).IsRequired();
            entity.Property(c => c.AlgorithmProfile).IsRequired().HasMaxLength(64);
        });
    }
}
