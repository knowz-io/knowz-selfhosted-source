namespace Knowz.SelfHosted.Application.DTOs;

public record VaultResponse(
    Guid Id,
    string Name,
    string? Description,
    string? VaultType,
    bool IsDefault,
    Guid? ParentVaultId,
    int? KnowledgeCount,
    DateTime CreatedAt);

public record VaultListResponse(List<VaultResponse> Vaults);

public record VaultContentsResponse(
    Guid VaultId,
    List<KnowledgeListItem> Items,
    int TotalItems);

public record CreateVaultResult(Guid Id, string Name, bool Created);

public record UpdateVaultResult(Guid Id, string Name, bool Updated);

public record DeleteVaultResult(Guid Id, bool Deleted);
