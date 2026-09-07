using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for knowledge versioning and audit logging.
/// Creates version snapshots on updates and supports restore to previous versions.
/// </summary>
public class VersioningService : IVersioningService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<VersioningService> _logger;

    public VersioningService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<VersioningService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<KnowledgeVersion> CreateVersionAsync(
        Guid knowledgeId, Guid? userId, string? changeDescription, CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;

        var knowledge = await _db.KnowledgeItems
            .FirstOrDefaultAsync(k => k.Id == knowledgeId, ct);

        if (knowledge == null)
            throw new InvalidOperationException($"Knowledge item {knowledgeId} not found.");

        // Determine next version number
        var maxVersion = await _db.KnowledgeVersions
            .Where(v => v.KnowledgeId == knowledgeId)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;

        var version = new KnowledgeVersion
        {
            TenantId = tenantId,
            KnowledgeId = knowledgeId,
            VersionNumber = maxVersion + 1,
            Title = knowledge.Title,
            Content = knowledge.Content,
            ContentType = knowledge.Type.ToString(),
            CreatedByUserId = userId,
            ChangeDescription = changeDescription
        };

        _db.KnowledgeVersions.Add(version);

        // Write audit log entry
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            EntityType = "Knowledge",
            EntityId = knowledgeId,
            Action = "VersionCreated",
            UserId = userId,
            Details = $"Version {version.VersionNumber} created. {changeDescription ?? ""}".Trim()
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created version {VersionNumber} for knowledge {KnowledgeId}",
            version.VersionNumber, knowledgeId);

        return version;
    }

    public async Task<List<KnowledgeVersion>> GetVersionsAsync(Guid knowledgeId, CancellationToken ct)
    {
        return await _db.KnowledgeVersions
            .Where(v => v.KnowledgeId == knowledgeId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    public async Task<KnowledgeVersion?> GetVersionAsync(Guid knowledgeId, int versionNumber, CancellationToken ct)
    {
        return await _db.KnowledgeVersions
            .FirstOrDefaultAsync(v => v.KnowledgeId == knowledgeId && v.VersionNumber == versionNumber, ct);
    }

    public async Task<bool> RestoreVersionAsync(Guid knowledgeId, int versionNumber, Guid? userId, CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;

        var version = await _db.KnowledgeVersions
            .FirstOrDefaultAsync(v => v.KnowledgeId == knowledgeId && v.VersionNumber == versionNumber, ct);

        if (version == null)
            return false;

        var knowledge = await _db.KnowledgeItems
            .FirstOrDefaultAsync(k => k.Id == knowledgeId, ct);

        if (knowledge == null)
            return false;

        // Snapshot current state before restoring (so the restore itself is versioned)
        var maxVersion = await _db.KnowledgeVersions
            .Where(v => v.KnowledgeId == knowledgeId)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;

        var restoreSnapshot = new KnowledgeVersion
        {
            TenantId = tenantId,
            KnowledgeId = knowledgeId,
            VersionNumber = maxVersion + 1,
            Title = knowledge.Title,
            Content = knowledge.Content,
            ContentType = knowledge.Type.ToString(),
            CreatedByUserId = userId,
            ChangeDescription = $"Restored from version {versionNumber}"
        };

        _db.KnowledgeVersions.Add(restoreSnapshot);

        // Apply the restore
        knowledge.Title = version.Title;
        knowledge.Content = version.Content;
        knowledge.UpdatedAt = DateTime.UtcNow;

        // Write audit log entry
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            EntityType = "Knowledge",
            EntityId = knowledgeId,
            Action = "VersionRestored",
            UserId = userId,
            Details = $"Restored to version {versionNumber}. New version {restoreSnapshot.VersionNumber} created as snapshot."
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Restored knowledge {KnowledgeId} to version {VersionNumber}, new snapshot version {NewVersion}",
            knowledgeId, versionNumber, restoreSnapshot.VersionNumber);

        return true;
    }

    /// <summary>
    /// Writes an audit log entry. Used by other services to record actions.
    /// </summary>
    public async Task WriteAuditLogAsync(
        string entityType, Guid entityId, string action,
        Guid? userId, string? userEmail, string? details,
        CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = _tenantProvider.TenantId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            UserId = userId,
            UserEmail = userEmail,
            Details = details
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gets paginated audit logs with optional filtering.
    /// </summary>
    public async Task<(List<AuditLog> Items, int TotalCount)> GetAuditLogsAsync(
        Guid? entityId, string? entityType,
        int page, int pageSize,
        CancellationToken ct)
    {
        var query = _db.AuditLogs.AsQueryable();

        if (entityId.HasValue)
            query = query.Where(a => a.EntityId == entityId.Value);

        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);

        query = query.OrderByDescending(a => a.Timestamp);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
