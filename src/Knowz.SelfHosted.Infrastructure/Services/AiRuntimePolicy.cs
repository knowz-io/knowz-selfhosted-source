using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>A host-owned offline switch takes precedence over retained database provider settings.</summary>
public static class AiRuntimePolicy
{
    public const string Setting = "KNOWZ_AI_DISABLED";
    public const string Guidance = "AI is disabled by the runtime host. Run knowz setup on the host and select a provider to enable AI; saved provider configuration is retained.";
    public static bool IsDisabled(IConfiguration configuration) =>
        string.Equals(configuration[Setting], "true", StringComparison.OrdinalIgnoreCase);
    public const string RevisionSetting = "KNOWZ_AI_CONFIG_UPDATED_AT";
    public static DateTimeOffset? ProviderRevision(IConfiguration configuration)
    {
        var value = configuration[RevisionSetting];
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var revision)) return revision.ToUniversalTime();
        throw new InvalidOperationException($"{RevisionSetting} must be an ISO UTC timestamp.");
    }
    public static bool IsSuperseded(string category, DateTime modifiedAt, DateTimeOffset? hostRevision) =>
        IsAiCategory(category) && hostRevision is not null && DateTime.SpecifyKind(modifiedAt, DateTimeKind.Utc) <= hostRevision.Value.UtcDateTime;
    public static bool IsAiCategory(string category) => category is
        "KnowzPlatform" or "OpenAiCompatible" or "AzureOpenAI" or "AzureAISearch" or "AzureAIVision" or "AzureDocumentIntelligence";
}
