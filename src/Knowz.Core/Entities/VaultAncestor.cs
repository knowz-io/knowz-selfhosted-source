namespace Knowz.Core.Entities;

public class VaultAncestor
{
    public Guid AncestorVaultId { get; set; }
    public virtual Vault AncestorVault { get; set; } = null!;
    public Guid DescendantVaultId { get; set; }
    public virtual Vault DescendantVault { get; set; } = null!;
    public int Depth { get; set; }
}
