using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Tenant CRUD operations for SuperAdmin management.
/// </summary>
public class TenantManagementService : ITenantManagementService
{
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<TenantManagementService> _logger;

    public TenantManagementService(SelfHostedDbContext db, ILogger<TenantManagementService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<TenantDto>> ListTenantsAsync()
    {
        return await _db.Tenants
            .Include(t => t.Users)
            .OrderBy(t => t.Name)
            .Select(t => new TenantDto
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                Description = t.Description,
                IsActive = t.IsActive,
                UserCount = t.Users.Count,
                CreatedAt = t.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<TenantDto?> GetTenantAsync(Guid id)
    {
        var tenant = await _db.Tenants
            .Include(t => t.Users)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return null;

        return MapToDto(tenant);
    }

    public async Task<TenantDto> CreateTenantAsync(CreateTenantRequest request)
    {
        // Check for duplicate slug
        var exists = await _db.Tenants.AnyAsync(t => t.Slug == request.Slug);
        if (exists)
        {
            throw new InvalidOperationException($"A tenant with slug '{request.Slug}' already exists.");
        }

        var tenant = new Tenant
        {
            Name = request.Name,
            Slug = request.Slug,
            Description = request.Description,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        // Create a default vault for the new tenant
        var defaultVault = new Vault
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Knowledge",
            Description = "Default knowledge vault",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Vaults.Add(defaultVault);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created tenant: {TenantName} ({TenantSlug}) with default vault {VaultId}",
            tenant.Name, tenant.Slug, defaultVault.Id);

        return MapToDto(tenant);
    }

    public async Task<TenantDto> UpdateTenantAsync(Guid id, UpdateTenantRequest request)
    {
        var tenant = await _db.Tenants
            .Include(t => t.Users)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            throw new KeyNotFoundException($"Tenant with ID '{id}' not found.");

        if (request.Name is not null)
            tenant.Name = request.Name;

        if (request.Slug is not null)
        {
            // Check for duplicate slug if changing
            if (request.Slug != tenant.Slug)
            {
                var slugExists = await _db.Tenants.AnyAsync(t => t.Slug == request.Slug && t.Id != id);
                if (slugExists)
                    throw new InvalidOperationException($"A tenant with slug '{request.Slug}' already exists.");
            }
            tenant.Slug = request.Slug;
        }

        if (request.Description is not null)
            tenant.Description = request.Description;

        if (request.IsActive.HasValue)
            tenant.IsActive = request.IsActive.Value;

        tenant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated tenant: {TenantId}", id);

        return MapToDto(tenant);
    }

    public async Task DeleteTenantAsync(Guid id)
    {
        var tenant = await _db.Tenants.FindAsync(id);

        if (tenant is null)
            throw new KeyNotFoundException($"Tenant with ID '{id}' not found.");

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted tenant: {TenantId} ({TenantName})", id, tenant.Name);
    }

    private static TenantDto MapToDto(Tenant tenant)
    {
        return new TenantDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Slug = tenant.Slug,
            Description = tenant.Description,
            IsActive = tenant.IsActive,
            UserCount = tenant.Users?.Count ?? 0,
            CreatedAt = tenant.CreatedAt
        };
    }
}
