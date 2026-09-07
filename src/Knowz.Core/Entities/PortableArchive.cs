namespace Knowz.Core.Entities;

public class PortableArchive
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid OriginalId { get; set; }
    public string JsonData { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
