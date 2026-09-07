using System.Security.Cryptography;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// User CRUD operations with API key generation and password reset for SuperAdmin management.
/// </summary>
public class UserManagementService : IUserManagementService
{
    private readonly SelfHostedDbContext _db;
    private readonly IAuthService _authService;
    private readonly ILogger<UserManagementService> _logger;

    private const string ApiKeyPrefix = "ksh_";
    private const int ApiKeyRandomLength = 32;

    public UserManagementService(
        SelfHostedDbContext db,
        IAuthService authService,
        ILogger<UserManagementService> logger)
    {
        _db = db;
        _authService = authService;
        _logger = logger;
    }

    public async Task<List<UserDto>> ListUsersAsync(Guid? tenantId = null)
    {
        var query = _db.Users.Include(u => u.Tenant).AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(u => u.TenantId == tenantId.Value);

        return await query
            .OrderBy(u => u.Username)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Role = u.Role,
                TenantId = u.TenantId,
                TenantName = u.Tenant.Name,
                IsActive = u.IsActive,
                MustChangePassword = u.MustChangePassword,
                ApiKey = u.ApiKey,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt
            })
            .ToListAsync();
    }

    public async Task<UserDto?> GetUserAsync(Guid id)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user is null)
            return null;

        return user.ToDto();
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request)
    {
        // Verify tenant exists
        var tenant = await _db.Tenants.FindAsync(request.TenantId);
        if (tenant is null)
            throw new KeyNotFoundException($"Tenant with ID '{request.TenantId}' not found.");

        // Check for duplicate username
        var usernameExists = await _db.Users.AnyAsync(u => u.Username == request.Username);
        if (usernameExists)
            throw new InvalidOperationException($"A user with username '{request.Username}' already exists.");

        var user = new User
        {
            TenantId = request.TenantId,
            Username = request.Username,
            PasswordHash = _authService.HashPassword(request.Password),
            Email = request.Email,
            DisplayName = request.DisplayName,
            Role = request.Role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Create membership for the user's home tenant
        var membership = new UserTenantMembership
        {
            UserId = user.Id,
            TenantId = request.TenantId,
            Role = request.Role,
            IsActive = true,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserTenantMemberships.Add(membership);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created user: {Username} in tenant: {TenantId}", user.Username, user.TenantId);

        // Reload with tenant for DTO mapping
        user.Tenant = tenant;
        return user.ToDto();
    }

    public async Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user is null)
            throw new KeyNotFoundException($"User with ID '{id}' not found.");

        if (request.Email is not null)
            user.Email = request.Email;

        if (request.DisplayName is not null)
            user.DisplayName = request.DisplayName;

        if (request.Role.HasValue)
            user.Role = request.Role.Value;

        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated user: {UserId}", id);

        return user.ToDto();
    }

    public async Task DeleteUserAsync(Guid id)
    {
        var user = await _db.Users.FindAsync(id);

        if (user is null)
            throw new KeyNotFoundException($"User with ID '{id}' not found.");

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted user: {UserId} ({Username})", id, user.Username);
    }

    public async Task<string> GenerateApiKeyAsync(Guid userId)
    {
        var user = await _db.Users.FindAsync(userId);

        if (user is null)
            throw new KeyNotFoundException($"User with ID '{userId}' not found.");

        var apiKey = GenerateApiKey();
        user.ApiKey = apiKey;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Generated new API key for user: {UserId}", userId);

        return apiKey;
    }

    public async Task RevokeApiKeyAsync(Guid userId)
    {
        var user = await _db.Users.FindAsync(userId);

        if (user is null)
            throw new KeyNotFoundException($"User with ID '{userId}' not found.");

        user.ApiKey = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Revoked API key for user: {UserId}", userId);
    }

    public async Task<string> ResetPasswordAsync(Guid userId, string newPassword)
    {
        var user = await _db.Users.FindAsync(userId);

        if (user is null)
            throw new KeyNotFoundException($"User with ID '{userId}' not found.");

        user.PasswordHash = _authService.HashPassword(newPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Password reset for user: {UserId}", userId);

        return "Password reset successfully";
    }

    // --- Multi-tenant membership methods ---

    public async Task<TenantMembershipDto> AddUserToTenantAsync(Guid userId, Guid tenantId, UserRole role)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            throw new KeyNotFoundException($"User with ID '{userId}' not found.");

        var tenant = await _db.Tenants.FindAsync(tenantId);
        if (tenant is null)
            throw new KeyNotFoundException($"Tenant with ID '{tenantId}' not found.");

        var existingMembership = await _db.UserTenantMemberships
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId);
        if (existingMembership)
            throw new InvalidOperationException($"User '{userId}' is already a member of tenant '{tenantId}'.");

        var membership = new UserTenantMembership
        {
            UserId = userId,
            TenantId = tenantId,
            Role = role,
            IsActive = true,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.UserTenantMemberships.Add(membership);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Added user {UserId} to tenant {TenantId} with role {Role}", userId, tenantId, role);

        return new TenantMembershipDto
        {
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            TenantSlug = tenant.Slug,
            Role = role,
            IsActive = true
        };
    }

    public async Task RemoveUserFromTenantAsync(Guid userId, Guid tenantId)
    {
        var membership = await _db.UserTenantMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);

        if (membership is null)
            throw new KeyNotFoundException($"Membership not found for user '{userId}' in tenant '{tenantId}'.");

        _db.UserTenantMemberships.Remove(membership);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Removed user {UserId} from tenant {TenantId}", userId, tenantId);
    }

    public async Task<TenantMembershipDto> UpdateUserTenantRoleAsync(Guid userId, Guid tenantId, UserRole role)
    {
        var membership = await _db.UserTenantMemberships
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);

        if (membership is null)
            throw new KeyNotFoundException($"Membership not found for user '{userId}' in tenant '{tenantId}'.");

        membership.Role = role;
        membership.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated role for user {UserId} in tenant {TenantId} to {Role}", userId, tenantId, role);

        return new TenantMembershipDto
        {
            TenantId = membership.TenantId,
            TenantName = membership.Tenant.Name,
            TenantSlug = membership.Tenant.Slug,
            Role = membership.Role,
            IsActive = membership.IsActive
        };
    }

    public async Task<List<TenantMembershipDto>> GetUserTenantsAsync(Guid userId)
    {
        return await _db.UserTenantMemberships
            .Include(m => m.Tenant)
            .Where(m => m.UserId == userId)
            .Select(m => new TenantMembershipDto
            {
                TenantId = m.TenantId,
                TenantName = m.Tenant.Name,
                TenantSlug = m.Tenant.Slug,
                Role = m.Role,
                IsActive = m.IsActive
            })
            .ToListAsync();
    }

    private static string GenerateApiKey()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var randomBytes = RandomNumberGenerator.GetBytes(ApiKeyRandomLength);
        var result = new char[ApiKeyRandomLength];

        for (int i = 0; i < ApiKeyRandomLength; i++)
        {
            result[i] = chars[randomBytes[i] % chars.Length];
        }

        return $"{ApiKeyPrefix}{new string(result)}";
    }

}
