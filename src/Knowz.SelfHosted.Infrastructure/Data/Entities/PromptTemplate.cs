namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

public class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PromptKey { get; set; } = string.Empty;
    public PromptScope Scope { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string TemplateText { get; set; } = string.Empty;
    public PromptMergeStrategy MergeStrategy { get; set; }
    public string? Description { get; set; }
    public bool IsSystemSeeded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? LastModifiedBy { get; set; }
}

public enum PromptScope
{
    Platform = 0,
    Tenant = 1,
    User = 2
}

public enum PromptMergeStrategy
{
    Override = 0,
    Supplement = 1
}

public static class PromptKeys
{
    public const string SystemPrompt = "SystemPrompt";
    public const string TitlePrompt = "TitlePrompt";
    public const string SummarizePrompt = "SummarizePrompt";
    public const string TagsPrompt = "TagsPrompt";
    public const string DocumentEditorPrompt = "DocumentEditorPrompt";
    public const string NoContextResponse = "NoContextResponse";

    public static readonly string[] All =
    {
        SystemPrompt, TitlePrompt, SummarizePrompt,
        TagsPrompt, DocumentEditorPrompt, NoContextResponse
    };

    /// <summary>Only SystemPrompt is user-supplementable. All others are admin-only.</summary>
    public static readonly string[] UserEligible = { SystemPrompt };
}
