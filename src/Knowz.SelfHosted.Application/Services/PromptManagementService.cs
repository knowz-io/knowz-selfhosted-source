using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public class PromptManagementService
{
    private readonly SelfHostedDbContext _db;
    private readonly PromptResolutionService _resolutionService;
    private readonly ILogger<PromptManagementService> _logger;

    public PromptManagementService(
        SelfHostedDbContext db,
        PromptResolutionService resolutionService,
        ILogger<PromptManagementService> logger)
    {
        _db = db;
        _resolutionService = resolutionService;
        _logger = logger;
    }

    // --- Platform scope (SuperAdmin) ---

    public async Task<List<PromptTemplate>> GetPlatformPromptsAsync(CancellationToken ct = default)
    {
        return await _db.PromptTemplates
            .Where(pt => pt.Scope == PromptScope.Platform)
            .OrderBy(pt => pt.PromptKey)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<PromptTemplate?> UpdatePlatformPromptAsync(
        string key, string templateText, string? description, string modifiedBy, CancellationToken ct = default)
    {
        if (!PromptKeys.All.Contains(key))
            return null;

        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt => pt.Scope == PromptScope.Platform && pt.PromptKey == key, ct);

        if (prompt == null) return null;

        prompt.TemplateText = templateText;
        if (description != null)
            prompt.Description = description;
        prompt.LastModifiedBy = modifiedBy;
        prompt.UpdatedAt = DateTime.UtcNow;
        prompt.IsSystemSeeded = false;

        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForPlatform();
        _logger.LogInformation("Platform prompt {Key} updated by {User}", key, modifiedBy);
        return prompt;
    }

    public async Task<PromptTemplate?> ResetPlatformPromptAsync(string key, string modifiedBy, CancellationToken ct = default)
    {
        if (!PromptKeys.All.Contains(key))
            return null;

        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt => pt.Scope == PromptScope.Platform && pt.PromptKey == key, ct);

        if (prompt == null) return null;

        prompt.TemplateText = GetDefaultText(key);
        prompt.IsSystemSeeded = true;
        prompt.LastModifiedBy = modifiedBy;
        prompt.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForPlatform();
        _logger.LogInformation("Platform prompt {Key} reset to default by {User}", key, modifiedBy);
        return prompt;
    }

    // --- Tenant scope (Admin) ---

    public async Task<List<PromptTemplate>> GetTenantPromptsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _db.PromptTemplates
            .Where(pt => pt.Scope == PromptScope.Tenant && pt.TenantId == tenantId)
            .OrderBy(pt => pt.PromptKey)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<PromptTemplate> UpsertTenantPromptAsync(
        Guid tenantId, string key, string templateText,
        PromptMergeStrategy mergeStrategy, string? description, string modifiedBy,
        CancellationToken ct = default)
    {
        if (!PromptKeys.All.Contains(key))
            throw new ArgumentException($"Invalid prompt key: {key}");

        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt =>
                pt.Scope == PromptScope.Tenant && pt.TenantId == tenantId && pt.PromptKey == key, ct);

        if (prompt == null)
        {
            prompt = new PromptTemplate
            {
                PromptKey = key,
                Scope = PromptScope.Tenant,
                TenantId = tenantId,
                TemplateText = templateText,
                MergeStrategy = mergeStrategy,
                Description = description,
                LastModifiedBy = modifiedBy,
            };
            _db.PromptTemplates.Add(prompt);
        }
        else
        {
            prompt.TemplateText = templateText;
            prompt.MergeStrategy = mergeStrategy;
            if (description != null)
                prompt.Description = description;
            prompt.LastModifiedBy = modifiedBy;
            prompt.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForTenant(tenantId);
        _logger.LogInformation("Tenant {TenantId} prompt {Key} upserted by {User}", tenantId, key, modifiedBy);
        return prompt;
    }

    public async Task<bool> DeleteTenantPromptAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt =>
                pt.Scope == PromptScope.Tenant && pt.TenantId == tenantId && pt.PromptKey == key, ct);

        if (prompt == null) return false;

        _db.PromptTemplates.Remove(prompt);
        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForTenant(tenantId);
        _logger.LogInformation("Tenant {TenantId} prompt {Key} deleted", tenantId, key);
        return true;
    }

    // --- User scope ---

    public async Task<List<PromptTemplate>> GetUserPromptsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.PromptTemplates
            .Where(pt => pt.Scope == PromptScope.User && pt.UserId == userId)
            .OrderBy(pt => pt.PromptKey)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<PromptTemplate> UpsertUserPromptAsync(
        Guid userId, Guid tenantId, string key, string templateText, string modifiedBy,
        CancellationToken ct = default)
    {
        if (!PromptKeys.UserEligible.Contains(key))
            throw new ArgumentException($"Prompt key '{key}' is not user-customizable");

        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt =>
                pt.Scope == PromptScope.User && pt.UserId == userId && pt.PromptKey == key, ct);

        if (prompt == null)
        {
            prompt = new PromptTemplate
            {
                PromptKey = key,
                Scope = PromptScope.User,
                TenantId = tenantId,
                UserId = userId,
                TemplateText = templateText,
                MergeStrategy = PromptMergeStrategy.Supplement,
                LastModifiedBy = modifiedBy,
            };
            _db.PromptTemplates.Add(prompt);
        }
        else
        {
            prompt.TemplateText = templateText;
            prompt.LastModifiedBy = modifiedBy;
            prompt.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForUser(userId);
        _logger.LogInformation("User {UserId} prompt {Key} upserted", userId, key);
        return prompt;
    }

    public async Task<bool> DeleteUserPromptAsync(Guid userId, string key, CancellationToken ct = default)
    {
        var prompt = await _db.PromptTemplates
            .FirstOrDefaultAsync(pt =>
                pt.Scope == PromptScope.User && pt.UserId == userId && pt.PromptKey == key, ct);

        if (prompt == null) return false;

        _db.PromptTemplates.Remove(prompt);
        await _db.SaveChangesAsync(ct);
        _resolutionService.InvalidateForUser(userId);
        _logger.LogInformation("User {UserId} prompt {Key} deleted", userId, key);
        return true;
    }

    // --- Resolved view ---

    public async Task<ResolvedPromptSet> GetResolvedPromptsAsync(
        Guid tenantId, Guid? userId, CancellationToken ct = default)
    {
        return await _resolutionService.ResolvePromptsAsync(tenantId, userId, ct);
    }

    private static string GetDefaultText(string key) => key switch
    {
        PromptKeys.SystemPrompt => DefaultPrompts.SystemPrompt,
        PromptKeys.TitlePrompt => DefaultPrompts.TitlePrompt,
        PromptKeys.SummarizePrompt => DefaultPrompts.DetailedSummarizePrompt,
        PromptKeys.TagsPrompt => DefaultPrompts.TagsPrompt,
        PromptKeys.DocumentEditorPrompt => DefaultPrompts.DocumentEditorPrompt,
        PromptKeys.NoContextResponse => DefaultPrompts.NoContextResponse,
        _ => string.Empty
    };
}
