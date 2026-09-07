namespace Knowz.Core.Entities;

public class KnowledgeLocation
{
    public Guid KnowledgeId { get; set; }
    public virtual Knowledge Knowledge { get; set; } = null!;
    public Guid LocationId { get; set; }
    public virtual Location Location { get; set; } = null!;
}
