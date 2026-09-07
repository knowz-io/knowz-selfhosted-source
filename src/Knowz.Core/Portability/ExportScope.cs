namespace Knowz.Core.Portability;

public class ExportScope
{
    public ExportScopeType Type { get; set; }
    public List<Guid>? VaultIds { get; set; }
    public List<Guid>? KnowledgeItemIds { get; set; }
}

public enum ExportScopeType
{
    Tenant,
    Vault,
    VaultWithChildren,
    MultiSelect,
    SingleItem
}

public enum ExportMode
{
    Light,
    Full
}
