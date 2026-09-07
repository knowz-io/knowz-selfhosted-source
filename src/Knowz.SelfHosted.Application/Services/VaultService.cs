using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for vault CRUD and content listing.
/// Uses ISelfHostedRepository for simple vault ops and DbContext for VaultAncestor closure table joins.
/// </summary>
public class VaultService
{
    private readonly ISelfHostedRepository<Vault> _vaultRepo;
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<VaultService> _logger;

    public VaultService(
        ISelfHostedRepository<Vault> vaultRepo,
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<VaultService> logger)
    {
        _vaultRepo = vaultRepo;
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<CreateVaultResult> CreateVaultAsync(
        string name, string? description, string? parentVaultIdStr, string? vaultTypeStr, CancellationToken ct)
    {
        Guid? parentVaultId = Guid.TryParse(parentVaultIdStr, out var pvid) ? pvid : null;

        var vault = new Vault
        {
            TenantId = _tenantProvider.TenantId,
            Name = name,
            Description = description ?? $"Vault for {name}",
            ParentVaultId = parentVaultId,
            VaultType = Enum.TryParse<VaultType>(vaultTypeStr, true, out var vt) ? vt : null
        };

        _db.Vaults.Add(vault);

        if (vault.ParentVaultId.HasValue)
        {
            var parentAncestors = await _db.VaultAncestors
                .Where(va => va.DescendantVaultId == vault.ParentVaultId.Value)
                .ToListAsync(ct);

            foreach (var ancestor in parentAncestors)
            {
                _db.VaultAncestors.Add(new VaultAncestor
                {
                    AncestorVaultId = ancestor.AncestorVaultId,
                    DescendantVaultId = vault.Id,
                    Depth = ancestor.Depth + 1
                });
            }

            _db.VaultAncestors.Add(new VaultAncestor
            {
                AncestorVaultId = vault.ParentVaultId.Value,
                DescendantVaultId = vault.Id,
                Depth = 1
            });
        }

        await _db.SaveChangesAsync(ct);

        return new CreateVaultResult(vault.Id, vault.Name, true);
    }

    public async Task<VaultResponse?> GetVaultAsync(Guid id, CancellationToken ct)
    {
        var vault = await _db.Vaults
            .Where(v => v.Id == id)
            .Select(v => new VaultResponse(
                v.Id, v.Name, v.Description,
                v.VaultType != null ? v.VaultType.ToString() : null,
                v.IsDefault,
                v.ParentVaultId,
                v.KnowledgeVaults.Count,
                v.CreatedAt))
            .FirstOrDefaultAsync(ct);

        return vault;
    }

    public async Task<UpdateVaultResult?> UpdateVaultAsync(
        Guid id, string? name, string? description, CancellationToken ct)
    {
        var vault = await _db.Vaults.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vault is null)
            return null;

        if (name is not null) vault.Name = name;
        if (description is not null) vault.Description = description;
        vault.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return new UpdateVaultResult(vault.Id, vault.Name, true);
    }

    public async Task<DeleteVaultResult?> DeleteVaultAsync(Guid id, CancellationToken ct)
    {
        var vault = await _db.Vaults.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vault is null)
            return null;

        vault.IsDeleted = true;
        vault.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new DeleteVaultResult(id, true);
    }

    public async Task<VaultListResponse> ListVaultsAsync(bool includeStats, CancellationToken ct)
    {
        if (includeStats)
        {
            var vaults = await _db.Vaults
                .OrderBy(v => v.Name)
                .Select(v => new VaultResponse(
                    v.Id, v.Name, v.Description,
                    v.VaultType != null ? v.VaultType.ToString() : null,
                    v.IsDefault,
                    v.ParentVaultId,
                    v.KnowledgeVaults.Count,
                    v.CreatedAt))
                .ToListAsync(ct);
            return new VaultListResponse(vaults);
        }
        else
        {
            var vaults = await _db.Vaults
                .OrderBy(v => v.Name)
                .Select(v => new VaultResponse(
                    v.Id, v.Name, v.Description,
                    v.VaultType != null ? v.VaultType.ToString() : null,
                    v.IsDefault,
                    v.ParentVaultId,
                    null,
                    v.CreatedAt))
                .ToListAsync(ct);
            return new VaultListResponse(vaults);
        }
    }

    public async Task<VaultContentsResponse> ListVaultContentsAsync(
        Guid vaultId, bool includeChildVaults, int limit, CancellationToken ct)
    {
        var vaultIds = new List<Guid> { vaultId };
        if (includeChildVaults)
        {
            var descendants = await _db.VaultAncestors
                .Where(va => va.AncestorVaultId == vaultId)
                .Select(va => va.DescendantVaultId)
                .ToListAsync(ct);
            vaultIds.AddRange(descendants);
        }

        var totalItems = await _db.KnowledgeVaults
            .Where(kv => vaultIds.Contains(kv.VaultId))
            .Select(kv => kv.KnowledgeId)
            .Distinct()
            .CountAsync(ct);

        var items = await _db.KnowledgeVaults
            .Where(kv => vaultIds.Contains(kv.VaultId))
            .Select(kv => kv.Knowledge)
            .Distinct()
            .OrderByDescending(k => k.UpdatedAt)
            .Take(limit)
            .Select(k => new KnowledgeListItem(
                k.Id, k.Title,
                k.Summary ?? (k.Content.Length > 200 ? k.Content.Substring(0, 200) : k.Content),
                k.Type.ToString(),
                k.FilePath, null, null, k.CreatedByUserId, null,
                k.CreatedAt, k.UpdatedAt,
                k.IsIndexed))
            .ToListAsync(ct);

        return new VaultContentsResponse(vaultId, items, totalItems);
    }
}
