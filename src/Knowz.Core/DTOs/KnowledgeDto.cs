namespace Knowz.Core.DTOs;

/// <summary>
/// Knowledge item DTO for API communication.
/// </summary>
public class KnowledgeDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? VaultName { get; set; }
    public string? TopicName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? AttachmentContext { get; set; }
}

public class CreateKnowledgeDto
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Category { get; set; }
    public List<string>? Tags { get; set; }
    public string? VaultName { get; set; }
    public string? TopicName { get; set; }
    public string? Source { get; set; }
}

public class UpdateKnowledgeDto
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Summary { get; set; }
    public string? Category { get; set; }
    public List<string>? Tags { get; set; }
    public string? VaultName { get; set; }
    public string? TopicName { get; set; }
}
