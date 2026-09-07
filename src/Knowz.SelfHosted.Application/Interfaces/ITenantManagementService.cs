using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// Tenant CRUD operations for SuperAdmin management.
/// </summary>
public interface ITenantManagementService
{
    Task<List<TenantDto>> ListTenantsAsync();
    Task<TenantDto?> GetTenantAsync(Guid id);
    Task<TenantDto> CreateTenantAsync(CreateTenantRequest request);
    Task<TenantDto> UpdateTenantAsync(Guid id, UpdateTenantRequest request);
    Task DeleteTenantAsync(Guid id);
}
