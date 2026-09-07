using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for CRUD operations on tags.
/// </summary>
public class TagService
{
    private readonly ISelfHostedRepository<Tag> _tagRepo;
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<TagService> _logger;

    public TagService(
        ISelfHostedRepository<Tag> tagRepo,
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<TagService> logger)
    {
        _tagRepo = tagRepo;
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<List<TagListItem>> ListTagsAsync(string? query, int limit, CancellationToken ct)
    {
        var dbQuery = _db.Tags.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
            dbQuery = dbQuery.Where(t => t.Name.Contains(query));

        var tags = await dbQuery
            .OrderBy(t => t.Name)
            .Take(limit)
            .Select(t => new TagListItem(
                t.Id,
                t.Name,
                t.KnowledgeItems.Count,
                t.CreatedAt))
            .ToListAsync(ct);

        return tags;
    }

    public async Task<TagListItem> CreateTagAsync(string name, CancellationToken ct)
    {
        var existing = await _db.Tags.AnyAsync(t => t.Name == name, ct);
        if (existing)
            throw new InvalidOperationException($"A tag with name '{name}' already exists.");

        var tag = new Tag
        {
            TenantId = _tenantProvider.TenantId,
            Name = name
        };

        _db.Tags.Add(tag);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created tag: {TagName} ({TagId})", tag.Name, tag.Id);

        return new TagListItem(tag.Id, tag.Name, 0, tag.CreatedAt);
    }

    public async Task<TagListItem?> UpdateTagAsync(Guid id, string name, CancellationToken ct)
    {
        var tag = await _tagRepo.GetByIdAsync(id, ct);
        if (tag is null)
            return null;

        var duplicate = await _db.Tags.AnyAsync(t => t.Name == name && t.Id != id, ct);
        if (duplicate)
            throw new InvalidOperationException($"A tag with name '{name}' already exists.");

        tag.Name = name;
        tag.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Updated tag: {TagId} -> {TagName}", id, name);

        var knowledgeCount = await _db.Tags
            .Where(t => t.Id == id)
            .Select(t => t.KnowledgeItems.Count)
            .FirstOrDefaultAsync(ct);

        return new TagListItem(tag.Id, tag.Name, knowledgeCount, tag.CreatedAt);
    }

    public async Task<DeleteResult?> DeleteTagAsync(Guid id, CancellationToken ct)
    {
        var tag = await _tagRepo.GetByIdAsync(id, ct);
        if (tag is null)
            return null;

        await _tagRepo.SoftDeleteAsync(tag, ct);
        await _tagRepo.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted tag: {TagId} ({TagName})", id, tag.Name);

        return new DeleteResult(id, true);
    }
}
