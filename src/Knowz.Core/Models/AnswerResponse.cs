namespace Knowz.Core.Models;

/// <summary>
/// Response model for ask_question operations.
/// </summary>
public class AnswerResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<Guid> SourceKnowledgeIds { get; set; } = new();
    public double Confidence { get; set; }
}
