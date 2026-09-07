namespace Knowz.Core.Entities;

public class KnowledgePerson
{
    public Guid KnowledgeId { get; set; }
    public virtual Knowledge Knowledge { get; set; } = null!;
    public Guid PersonId { get; set; }
    public virtual Person Person { get; set; } = null!;
    public string? RelationshipContext { get; set; }
    public string? Role { get; set; }
    public int Mentions { get; set; }
    public double? ConfidenceScore { get; set; }
}
