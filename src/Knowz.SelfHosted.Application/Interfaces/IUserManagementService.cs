using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// User CRUD operations with API key generation and password reset for SuperAdmin management.
/// </summary>
public interface IUserManagementService
{
    Task<List<UserDto>> ListUsersAsync(Guid? tenantId = null);
    Task<UserDto?> GetUserAsync(Guid id);
    Task<UserDto> CreateUserAsync(CreateUserRequest request);
    Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request);
    Task DeleteUserAsync(Guid id);
    Task<string> GenerateApiKeyAsync(Guid userId);
    Task RevokeApiKeyAsync(Guid userId);
    Task<string> ResetPasswordAsync(Guid userId, string newPassword);

    // --- Multi-tenant membership ---
    Task<TenantMembershipDto> AddUserToTenantAsync(Guid userId, Guid tenantId, UserRole role);
    Task RemoveUserFromTenantAsync(Guid userId, Guid tenantId);
    Task<TenantMembershipDto> UpdateUserTenantRoleAsync(Guid userId, Guid tenantId, UserRole role);
    Task<List<TenantMembershipDto>> GetUserTenantsAsync(Guid userId);
}
