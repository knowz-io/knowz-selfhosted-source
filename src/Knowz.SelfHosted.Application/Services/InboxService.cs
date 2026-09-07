using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for inbox item CRUD and convert-to-knowledge operations.
/// Uses ISelfHostedRepository for persistence and SelfHostedDbContext for LIKE queries.
/// </summary>
public class InboxService
{
    private readonly ISelfHostedRepository<InboxItem> _inboxRepo;
    private readonly SelfHostedDbContext _db;
    private readonly KnowledgeService _knowledgeService;
    private readonly ITenantProvider _tenantProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InboxService> _logger;

    public InboxService(
        ISelfHostedRepository<InboxItem> inboxRepo,
        SelfHostedDbContext db,
        KnowledgeService knowledgeService,
        ITenantProvider tenantProvider,
        IConfiguration configuration,
        ILogger<InboxService> logger)
    {
        _inboxRepo = inboxRepo;
        _db = db;
        _knowledgeService = knowledgeService;
        _tenantProvider = tenantProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<InboxItemResult> CreateInboxItemAsync(string body, Guid? createdByUserId, CancellationToken ct)
    {
        var item = new InboxItem
        {
            TenantId = _tenantProvider.TenantId,
            Body = body,
            CreatedByUserId = createdByUserId
        };

        await _inboxRepo.AddAsync(item, ct);
        await _inboxRepo.SaveChangesAsync(ct);

        return new InboxItemResult(item.Id, true);
    }

    public async Task<InboxListResponse> ListInboxItemsAsync(
        int page, int pageSize, string? search, string? type,
        Guid? callerUserId, bool isCallerAdmin,
        CancellationToken ct)
    {
        var query = _db.InboxItems.AsQueryable();

        // Apply user scoping based on config
        var scope = _configuration["Inbox:VisibilityScope"] ?? "Shared";
        if (scope.Equals("PerUser", StringComparison.OrdinalIgnoreCase)
            && !isCallerAdmin
            && callerUserId.HasValue)
        {
            query = query.Where(i => i.CreatedByUserId == callerUserId.Value || i.CreatedByUserId == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // EF InMemory does not support EF.Functions.Like, so use Contains for compatibility.
            // In production (SQL), EF translates Contains to LIKE '%value%'.
            var searchLower = search.ToLowerInvariant();
            query = query.Where(i => i.Body.ToLower().Contains(searchLower));
        }

        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<InboxItemType>(type, true, out var itemType))
        {
            query = query.Where(i => i.Type == itemType);
        }

        query = query.OrderByDescending(i => i.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new InboxItemDto(
                i.Id,
                i.Body,
                i.Type.ToString(),
                i.CreatedByUserId,
                i.CreatedAt,
                i.UpdatedAt))
            .ToListAsync(ct);

        var totalPages = pageSize > 0 ? (int)Math.Ceiling((double)total / pageSize) : 0;
        return new InboxListResponse(items, page, pageSize, total, totalPages, scope);
    }

    public async Task<InboxItemDto?> GetInboxItemAsync(Guid id, CancellationToken ct)
    {
        var item = await _inboxRepo.GetByIdAsync(id, ct);
        if (item == null)
            return null;

        return new InboxItemDto(item.Id, item.Body, item.Type.ToString(), item.CreatedByUserId, item.CreatedAt, item.UpdatedAt);
    }

    public async Task<InboxItemDto?> UpdateInboxItemAsync(Guid id, string body, CancellationToken ct)
    {
        var item = await _inboxRepo.GetByIdAsync(id, ct);
        if (item == null)
            return null;

        item.Body = body;
        await _inboxRepo.UpdateAsync(item, ct);
        await _inboxRepo.SaveChangesAsync(ct);

        return new InboxItemDto(item.Id, item.Body, item.Type.ToString(), item.CreatedByUserId, item.CreatedAt, item.UpdatedAt);
    }

    public async Task<DeleteResult?> DeleteInboxItemAsync(Guid id, CancellationToken ct)
    {
        var item = await _inboxRepo.GetByIdAsync(id, ct);
        if (item == null)
            return null;

        await _inboxRepo.SoftDeleteAsync(item, ct);
        await _inboxRepo.SaveChangesAsync(ct);

        return new DeleteResult(id, true);
    }

    public async Task<ConvertToKnowledgeResult?> ConvertToKnowledgeAsync(
        Guid id, string? vaultId, List<string>? tags, CancellationToken ct)
    {
        var item = await _inboxRepo.GetByIdAsync(id, ct);
        if (item == null)
            return null;

        // Create knowledge item via KnowledgeService. Leave as placeholder so the
        // enrichment pipeline generates a real AI title from the body content.
        var title = "Untitled";
        var knowledgeResult = await _knowledgeService.CreateKnowledgeAsync(
            content: item.Body,
            title: title,
            typeStr: "Note",
            vaultIdStr: vaultId,
            tagNames: tags ?? new List<string>(),
            source: "inbox",
            ct);

        // Soft-delete the inbox item after successful conversion
        await _inboxRepo.SoftDeleteAsync(item, ct);
        await _inboxRepo.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Converted inbox item {InboxId} to knowledge {KnowledgeId}",
            id, knowledgeResult.Id);

        return new ConvertToKnowledgeResult(id, knowledgeResult.Id, knowledgeResult.Title, true);
    }

    public async Task<BatchConvertResult> BatchConvertToKnowledgeAsync(
        List<Guid> ids, string? vaultId, List<string>? tags, CancellationToken ct)
    {
        if (ids.Count > 50)
            throw new ArgumentException("Batch convert is limited to 50 items at a time.");

        var results = new List<ConvertToKnowledgeResult>();
        var converted = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            try
            {
                var result = await ConvertToKnowledgeAsync(id, vaultId, tags, ct);
                if (result != null)
                {
                    results.Add(result);
                    converted++;
                }
                else
                {
                    results.Add(new ConvertToKnowledgeResult(id, Guid.Empty, string.Empty, false));
                    failed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert inbox item {Id} to knowledge", id);
                results.Add(new ConvertToKnowledgeResult(id, Guid.Empty, string.Empty, false));
                failed++;
            }
        }

        return new BatchConvertResult(ids.Count, converted, failed, results);
    }
}
