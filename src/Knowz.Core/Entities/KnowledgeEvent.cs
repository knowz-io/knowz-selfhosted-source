namespace Knowz.Core.Entities;

public class KnowledgeEvent
{
    public Guid KnowledgeId { get; set; }
    public virtual Knowledge Knowledge { get; set; } = null!;
    public Guid EventId { get; set; }
    public virtual Event Event { get; set; } = null!;
}
