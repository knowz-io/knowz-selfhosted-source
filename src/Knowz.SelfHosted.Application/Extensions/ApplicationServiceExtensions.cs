using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Knowz.SelfHosted.Application.Extensions;

public static class ApplicationServiceExtensions
{
    /// <summary>
    /// Registers all self-hosted application services and the generic repository.
    /// </summary>
    public static IServiceCollection AddSelfHostedApplication(this IServiceCollection services)
    {
        // Generic repository — one line covers all entities
        services.AddScoped(typeof(ISelfHostedRepository<>), typeof(SelfHostedRepository<>));

        // Chunking service
        services.AddScoped<ISelfHostedChunkingService, SelfHostedChunkingService>();
        services.AddScoped<IVaultAccessService, VaultAccessService>();

        // Versioning and audit
        services.AddScoped<IVersioningService, VersioningService>();
        services.AddScoped<VersioningService>();

        // Application services
        services.AddScoped<KnowledgeService>();
        services.AddScoped<SearchFacade>();
        services.AddScoped<VaultService>();
        services.AddScoped<TopicService>();
        services.AddScoped<EntityService>();
        services.AddScoped<TagService>();
        services.AddScoped<InboxService>();
        services.AddScoped<FileStorageService>();
        services.AddScoped<CommentService>();
        // File-cleanup policy is config-backed (FileCleanup:Mode), default PromptUser.
        // WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
        services.AddScoped<IFileCleanupPolicyResolver, FileCleanupPolicyResolver>();
        services.AddScoped<IUserPreferencesService, UserPreferencesService>();

        // Portability services
        services.AddScoped<IPortableExportService, PortableExportService>();
        services.AddScoped<IPortableImportService, PortableImportService>();

        // Vault sync services
        services.AddScoped<IVaultSyncOrchestrator, VaultSyncOrchestrator>();
        services.AddScoped<IPlatformSyncClient, PlatformSyncClient>();
        // One credential resolver + named-client factory for entity sync AND file sync
        // (NodeID FIX_SelfHostedFileSyncAuth).
        services.AddScoped<PlatformSyncCredentialResolver>();
        services.AddScoped<IPlatformAuditLog, PlatformAuditLogService>();
        // Per-tenant platform credential store (NodeID: PlatformSyncConnection).
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddSingleton<IUrlValidator, PlatformUrlValidator>();
        // Rate limiter is a singleton — sliding-window state must persist across requests
        // (V-SEC-09). NodeID PlatformSyncItemOps.
        services.AddSingleton<IPlatformSyncRateLimiter, PlatformSyncRateLimiter>();
        services.AddScoped<VaultScopedExportService>();
        services.AddScoped<FileSyncService>();

        // Content extraction — composite pattern (routes by content type)
        // Order: AI-powered extractors first (they return CanExtract=false when NoOp),
        // then native fallbacks (PdfPig, OpenXML)
        services.AddScoped<TextFileContentExtractor>();
        services.AddScoped<PdfContentExtractor>();
        services.AddScoped<DocxContentExtractor>();
        services.AddScoped<ExcelContentExtractor>();
        services.AddScoped<PowerPointContentExtractor>();
        services.AddScoped<ImageContentExtractor>();
        // DocumentIntelligenceContentExtractor is registered by AddDocumentIntelligence()

        services.AddScoped<IFileContentExtractor>(sp =>
        {
            var extractors = new List<IFileContentExtractor>
            {
                sp.GetRequiredService<TextFileContentExtractor>(),       // text/* — always first, fastest
            };

            // AI-powered extractors go before native fallbacks.
            // Their CanExtract returns false when provider is NoOp,
            // so CompositeContentExtractor falls through to native extractors.
            var diExtractor = sp.GetService<DocumentIntelligenceContentExtractor>();
            if (diExtractor != null)
                extractors.Add(diExtractor);                             // AI-powered doc extraction (PDF, images via DocIntel)

            extractors.Add(sp.GetRequiredService<ImageContentExtractor>());  // AI-powered vision analysis

            // Native fallbacks
            extractors.Add(sp.GetRequiredService<PdfContentExtractor>());    // Native PDF fallback (PdfPig)
            extractors.Add(sp.GetRequiredService<DocxContentExtractor>());   // Native DOCX fallback (OpenXML)
            extractors.Add(sp.GetRequiredService<ExcelContentExtractor>());
            extractors.Add(sp.GetRequiredService<PowerPointContentExtractor>());

            return new CompositeContentExtractor(extractors);
        });

        // Enrichment outbox writer
        services.AddScoped<IEnrichmentOutboxWriter, EnrichmentOutboxWriter>();

        // Prompt resolution (singleton — owns its own in-memory cache with 5-min TTL)
        services.AddSingleton<PromptResolutionService>();
        services.AddScoped<PromptManagementService>();

        // Configuration management
        services.AddScoped<IConfigurationManagementService, ConfigurationManagementService>();

        // Git sync service (implements IGitSyncService for background service resolution)
        services.AddScoped<GitSyncService>();
        services.AddScoped<IGitSyncService>(sp => sp.GetRequiredService<GitSyncService>());

        // Git commit-history ingestion (NODE-4)
        // ICommitElaborationLlmClient is registered in OpenAIExtensions alongside the
        // IOpenAIService tier selection (NoOp vs Platform vs Azure).
        services.AddScoped<ICommitSecretScanner, CommitSecretScanner>();
        services.AddScoped<ICommitElaborationPromptBuilder, CommitElaborationPromptBuilder>();
        // CommitRelinkService depends on the concrete GitCommitHistoryService (for the
        // internal ResolveAndLinkChangedFilesAsync helper), so we need both the concrete
        // type and the interface registered from the same scoped instance.
        services.AddScoped<GitCommitHistoryService>();
        services.AddScoped<IGitCommitHistoryService>(sp => sp.GetRequiredService<GitCommitHistoryService>());

        // Git commit backfill / relink endpoint (NODE-3 CommitBackfillEndpoint)
        // WorkGroupID: kc-feat-commit-history-polish-20260411-051000
        services.AddScoped<CommitRelinkService>();

        return services;
    }
}
