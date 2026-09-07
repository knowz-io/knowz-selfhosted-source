namespace Knowz.Core.Entities;

public class KnowledgeVault
{
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public virtual Knowledge Knowledge { get; set; } = null!;
    public Guid VaultId { get; set; }
    public virtual Vault Vault { get; set; } = null!;
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
