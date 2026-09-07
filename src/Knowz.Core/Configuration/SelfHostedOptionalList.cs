namespace Knowz.Core.Configuration;

/// <summary>
/// Single source of truth for self-hosted service classes that are allowed to fail
/// DI validation without crashing the host. Items here are expected to degrade to
/// NoOp or warn-and-continue when their underlying Azure dependency is missing.
///
/// Consumed by <c>Program.cs</c> (passed to <c>StartupDependencyValidator</c>) and by
/// <c>SelfHostedStartupTests</c> so runtime + test optional lists cannot drift — the
/// same drift that caused the 2026-04-18 dev Functions crash loop on the main platform
/// (<c>MemoryNotificationFunction</c> optional in tests / critical at runtime).
///
/// Required vs optional split (SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 2):
/// - Required (hard-fail): SelfHostedDbContext, IOpenAIService, ISearchService, IFileStorageProvider
/// - Optional (warn-and-continue): IAttachmentAIProvider, DocumentIntelligenceContentExtractor
///
/// The optional list uses class/interface short names because the validator's
/// reflection path compares against <c>Type.Name</c>.
/// </summary>
public static class SelfHostedOptionalList
{
    /// <summary>
    /// Self-hosted services that MAY be absent without crashing startup.
    /// </summary>
    public static readonly string[] Default =
    [
        "IAttachmentAIProvider",                 // Requires Azure AI Vision / Document Intelligence
        "DocumentIntelligenceContentExtractor",  // Requires Azure Document Intelligence
    ];
}
