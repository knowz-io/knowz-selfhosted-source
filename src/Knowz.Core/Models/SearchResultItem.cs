namespace Knowz.Core.Models;

/// <summary>
/// Search result model shared between search and OpenAI services.
/// </summary>
public class SearchResultItem
{
    public Guid KnowledgeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? VaultName { get; set; }
    public string? TopicName { get; set; }
    public string? KnowledgeType { get; set; }
    public string? FilePath { get; set; }
    public List<string> Tags { get; set; } = new();
    public double Score { get; set; }
    public double? SemanticScore { get; set; }
    public List<string> Highlights { get; set; } = new();
    public int Position { get; set; }
    public string? DocumentType { get; set; }

    /// <summary>
    /// Knowledge creation date (ingestion timestamp). Default MinValue if the
    /// producing search backend did not populate it. Consumers (e.g. chat
    /// context formatters) should suppress the field when it equals default.
    /// Added by FEAT_SelfHostedTemporalAwareness (WorkGroup
    /// kc-feat-selfhosted-temporal-aware-20260410-231702).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last-edit timestamp. Null when the producer did not populate it, or
    /// when the knowledge item has never been updated. Consumers should
    /// suppress this field when null OR when it falls on the same calendar
    /// day as CreatedAt in the user's timezone (same-day update suppression).
    /// Added by FEAT_SelfHostedTemporalAwareness.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
