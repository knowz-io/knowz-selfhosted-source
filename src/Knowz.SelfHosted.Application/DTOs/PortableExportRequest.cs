namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>Shared wire shape with the platform export API, without a hosted assembly dependency.</summary>
public class PortableExportRequest
{
    public string ScopeType { get; set; } = "Vault";
    public List<Guid>? VaultIds { get; set; }
    public List<Guid>? KnowledgeItemIds { get; set; }
    public string Mode { get; set; } = "Light";
}
