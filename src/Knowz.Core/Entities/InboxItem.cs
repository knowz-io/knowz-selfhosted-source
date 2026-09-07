using Knowz.Core.Enums;
using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class InboxItem : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Body { get; set; } = string.Empty;
    public InboxItemType Type { get; set; } = InboxItemType.Note;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }
}
