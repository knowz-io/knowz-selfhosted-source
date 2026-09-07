using Knowz.Core.Enums;
using Knowz.Core.Interfaces;

namespace Knowz.Core.Entities;

public class Vault : ISelfHostedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public VaultType? VaultType { get; set; }
    public bool IsDefault { get; set; }

    // Hierarchy
    public Guid? ParentVaultId { get; set; }
    public virtual Vault? ParentVault { get; set; }
    public virtual ICollection<Vault> Children { get; set; } = new List<Vault>();

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? PlatformData { get; set; }

    // Navigation
    public virtual ICollection<KnowledgeVault> KnowledgeVaults { get; set; } = new List<KnowledgeVault>();
    public virtual ICollection<VaultPerson> VaultPersons { get; set; } = new List<VaultPerson>();
    public virtual ICollection<VaultAncestor> Ancestors { get; set; } = new List<VaultAncestor>();
    public virtual ICollection<VaultAncestor> Descendants { get; set; } = new List<VaultAncestor>();
}
