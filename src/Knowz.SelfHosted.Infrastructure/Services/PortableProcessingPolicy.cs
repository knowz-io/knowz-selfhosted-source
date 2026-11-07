using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>Live typed protection state overrides queued work, stale chunks and external indexes.</summary>
public static class PortableProcessingPolicy
{
    public const string BlockedMessage = "Knowledge is protected or unavailable for server AI and search in the current scope.";
    public static IQueryable<Knowledge> AllowedKnowledge(SelfHostedDbContext db) => db.KnowledgeItems
        .Where(k => k.PortableRecoveryState == null && !db.ShSealedKnowledge.Any(s => s.TenantId == k.TenantId && s.KnowledgeId == k.Id)
            && !db.Tenants.Any(t => t.Id == k.TenantId && !t.IsActive));

    public static Task<bool> CanProcessAsync(SelfHostedDbContext db, Guid id, Guid tenantId, CancellationToken ct) =>
        AllowedKnowledge(db).AsNoTracking().AnyAsync(k => k.Id == id && k.TenantId == tenantId, ct);

    public static Task<bool> IsProtectedAsync(SelfHostedDbContext db, Guid id, CancellationToken ct) => db.KnowledgeItems.IgnoreQueryFilters()
        .AnyAsync(k => k.TenantId == db.CurrentTenantId && k.Id == id && !k.IsDeleted
            && (k.PortableRecoveryState != null || db.ShSealedKnowledge.IgnoreQueryFilters().Any(s => s.TenantId == k.TenantId && s.KnowledgeId == k.Id)), ct);

    public static string PublicFileName(string name, string? encryptedFields)
    {
        if (encryptedFields == null) return name;
        try
        {
            using var json = StrictJson.Parse(encryptedFields, 1024 * 1024);
            return json.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == System.Text.Json.JsonValueKind.Object
                && !fields.TryGetProperty("fileName", out _) ? name : "encrypted-file";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { return "encrypted-file"; }
    }

    public static async Task DiscardStaleIndexAsync(SelfHostedDbContext db, Guid id, ISearchService search, CancellationToken ct)
    {
        var chunks = await db.ContentChunks.IgnoreQueryFilters().Where(c => c.TenantId == db.CurrentTenantId && c.KnowledgeId == id).ToListAsync(ct);
        db.ContentChunks.RemoveRange(chunks); await db.SaveChangesAsync(ct);
        // Delete IDs/chunk positions only; no protected content is sent to the external provider.
        await search.DeleteDocumentWithChunksAsync(id, chunks.Select(c => c.Position), ct);
    }
}

internal sealed class ProtectedKnowledgeProcessingException() : InvalidOperationException(PortableProcessingPolicy.BlockedMessage);
