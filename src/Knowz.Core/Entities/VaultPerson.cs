namespace Knowz.Core.Entities;

public class VaultPerson
{
    public Guid VaultId { get; set; }
    public virtual Vault Vault { get; set; } = null!;
    public Guid PersonId { get; set; }
    public virtual Person Person { get; set; } = null!;
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}
