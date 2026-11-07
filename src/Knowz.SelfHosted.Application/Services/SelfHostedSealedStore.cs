using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public sealed record SealedStoreResult(bool Ok, int StatusCode, string? Error, Guid? Id = null, int Written = 0)
{
    public static SealedStoreResult Success(Guid? id, int status, int written) => new(true, status, null, id, written);
    public static SealedStoreResult Fail(int status, string error) => new(false, status, error);
}

public sealed class SealedItemWrite
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PortableKnowledge? PlainItem { get; set; }
    public string? Label { get; set; }
    public Guid? VaultId { get; set; }
    public SealedKnowledgeItem Item { get; set; } = new();
    public ExportedCryptoContext? Context { get; set; }
    public ExportedCryptoKeyIdentity? PublicKey { get; set; }
    public List<PortablePayloadKeyReference>? PayloadKeyReferences { get; set; }
}

public sealed class LocalUnlockAck
{
    public string? Fingerprint { get; set; }
    public bool Verified { get; set; }
}

public sealed record SealedItemResponse(
    Guid Id,
    Guid OriginalItemId,
    string LifecycleStatus,
    string? VerifiedFingerprint,
    DateTime? ClientVerifiedAt,
    SealedKnowledgeItem Envelope,
    string Revision);

public sealed class SealedCommentWrite
{
    public Guid? VaultId { get; set; }
    public List<PortablePayloadKeyReference>? PayloadKeyReferences { get; set; }
    public Guid? KnowledgeId { get; set; }
    public SealedCommentRecord Comment { get; set; } = new();
}

public sealed record SealedCommentResponse(
    Guid Id,
    Guid OriginalCommentId,
    Guid KnowledgeId,
    Guid? ParentCommentId,
    SealedCommentRecord Envelope);

/// <summary>
/// Public self-hosted sealed CRUD. Stores ciphertext and public key catalogs.
/// Does not decrypt, does not accept private keys, and does not index envelope bytes.
/// </summary>
public partial class SelfHostedSealedStore
{
    public const string LockedStatus = "preserved-locked";
    public const string ClientVerifiedStatus = "client-verified";
    public const int MaxEnvelopeChars = 2_000_000;

    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly ILogger<SelfHostedSealedStore> _logger;

    public SelfHostedSealedStore(
        SelfHostedDbContext db,
        ITenantProvider tenant,
        ILogger<SelfHostedSealedStore> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    public Task<SealedStoreResult> CreateAsync(SealedItemWrite write, CancellationToken ct, string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.PayloadKeyReferences != null) return CreateGraphItemAsync(write, expectedRevision, ct);
        var package = new PortableExportPackage
        {
            SchemaVersion = EncryptedPortabilityProfile.SchemaVersion,
            RequiredCapabilities = new List<string> { EncryptedPortabilityProfile.Capability },
            SourceEdition = "selfhosted",
            SourceTenantId = _tenant.TenantId,
            ExportedAt = DateTime.UtcNow,
            SealedItems = new List<SealedKnowledgeItem> { write.Item },
            CryptoContexts = write.Context == null ? null : new List<ExportedCryptoContext> { write.Context },
            CryptoKeyCatalog = write.PublicKey == null ? null : new List<ExportedCryptoKeyIdentity> { write.PublicKey },
        };
        return ImportPackageAsync(package, write.VaultId, write.Label, ct);
    }

    public Task<SealedStoreResult> ImportPackageAsync(PortableExportPackage package, Guid? destinationVaultId, CancellationToken ct)
        => ImportPackageAsync(package, destinationVaultId, label: null, ct);

    public async Task<SealedItemResponse?> GetAsync(Guid knowledgeId, CancellationToken ct)
    {
        var row = await _db.ShSealedKnowledge.AsNoTracking()
            .FirstOrDefaultAsync(s => s.KnowledgeId == knowledgeId, ct);
        return row == null ? null : ToResponse(row);
    }

    public async Task<SealedStoreResult> UpdateAsync(Guid knowledgeId, SealedItemWrite write, CancellationToken ct, string? expectedRevision = null)
    {
        if (write.PlainItem != null) return await UpdateCurrentFieldsAsync(knowledgeId, write, expectedRevision, ct);
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        ArgumentNullException.ThrowIfNull(write);
        var problem = ValidateItem(write.Item);
        if (problem != null)
            return SealedStoreResult.Fail(400, problem);

        var row = await _db.ShSealedKnowledge.FirstOrDefaultAsync(s => s.KnowledgeId == knowledgeId, ct);
        if (row == null)
            return SealedStoreResult.Fail(404, "Sealed item not found.");
        if ((expectedRevision != null || row.LineageScope.Length > 0) && expectedRevision?.Trim('"') != ItemRevision(row))
            return SealedStoreResult.Fail(409, "The encrypted item changed. Reload before editing.");
        if (row.LineageScope.Length > 0)
        {
            write.Item.Id = row.KnowledgeId;
            var invalid = await ValidateWriteReferencesAsync(row, write, ct);
            if (invalid != null) return SealedStoreResult.Fail(400, invalid);
        }
        if (write.Item.OriginalItemId != Guid.Empty && write.Item.OriginalItemId != row.OriginalItemId)
            return SealedStoreResult.Fail(400, "Original item id cannot change.");

        await SnapshotAsync(row, ct);
        ApplyEnvelope(row, write.Item);
        if (write.PayloadKeyReferences != null) await ReplaceReferencesAsync("knowledge", knowledgeId, write.PayloadKeyReferences, ct);
        var knowledge = await _db.KnowledgeItems.FirstOrDefaultAsync(k => k.Id == knowledgeId, ct);
        if (knowledge != null)
        {
            knowledge.Content = string.Empty;
            if (!string.IsNullOrWhiteSpace(write.Label))
                knowledge.Title = LabelOrDefault(write.Label);
            knowledge.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Updated sealed item {KnowledgeId} for tenant {TenantId}. Server decrypt was not performed.",
            knowledgeId, _tenant.TenantId);
        return SealedStoreResult.Success(knowledgeId, 200, 1);
    }

    public async Task<SealedStoreResult> RecordLocalUnlockAsync(Guid knowledgeId, LocalUnlockAck ack, CancellationToken ct)
    {
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        ArgumentNullException.ThrowIfNull(ack);
        var row = await _db.ShSealedKnowledge.FirstOrDefaultAsync(s => s.KnowledgeId == knowledgeId, ct);
        if (row == null)
            return SealedStoreResult.Fail(404, "Sealed item not found.");

        var before = row.Ciphertext;
        if (ack.Verified)
        {
            var fingerprint = ack.Fingerprint?.Trim();
            if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 128)
                return SealedStoreResult.Fail(400, "A public fingerprint is required to record local verification.");
            row.LifecycleStatus = ClientVerifiedStatus;
            row.VerifiedFingerprint = fingerprint;
            row.ClientVerifiedAt = DateTime.UtcNow;
        }
        else
        {
            row.LifecycleStatus = LockedStatus;
            row.VerifiedFingerprint = null;
            row.ClientVerifiedAt = null;
        }

        row.Ciphertext = before;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Recorded local unlock acknowledgement for {KnowledgeId}. Ciphertext was not unwrapped.",
            knowledgeId);
        return SealedStoreResult.Success(knowledgeId, 200, 1);
    }

    public async Task<SealedStoreResult> DeleteAsync(Guid knowledgeId, CancellationToken ct, string? expectedRevision = null)
    {
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        var row = await _db.ShSealedKnowledge.FirstOrDefaultAsync(s => s.KnowledgeId == knowledgeId, ct);
        if (row == null)
            return SealedStoreResult.Fail(404, "Sealed item not found.");

        if ((row.LineageScope.Length > 0 || expectedRevision != null) && expectedRevision?.Trim('"') != ItemRevision(row)) return SealedStoreResult.Fail(409, "Encrypted item changed; reload before deleting.");
        var versions = await _db.ShSealedVersions.Where(v => v.KnowledgeId == knowledgeId).ToListAsync(ct);
        var files = await _db.ShSealedFiles.Where(f => f.KnowledgeId == knowledgeId).ToListAsync(ct);
        var comments = await _db.ShSealedComments.Where(c => c.KnowledgeId == knowledgeId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var version in versions) { version.IsDeleted = true; version.UpdatedAt = now; }
        foreach (var comment in comments) { comment.IsDeleted = true; comment.UpdatedAt = now; }
        var commentIds = comments.Select(c => c.Id).ToArray();
        var attachments = await _db.ShSealedAttachments.Where(a => a.KnowledgeId == knowledgeId || a.CommentId.HasValue && commentIds.Contains(a.CommentId.Value)).ToListAsync(ct);
        await HideAttachmentsAndRetainFilesAsync(attachments, knowledgeId, ct);
        foreach (var file in files)
        {
            file.KnowledgeId = null;
            file.UpdatedAt = now;
        }
        var versionIds = versions.Select(v => v.Id).ToArray();
        var attachmentIds = attachments.Select(a => a.Id).ToArray();
        foreach (var reference in await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == knowledgeId
            || r.OwnerKind == "comment" && commentIds.Contains(r.OwnerId) || r.OwnerKind == "version" && versionIds.Contains(r.OwnerId)
            || r.OwnerKind == "attachment" && attachmentIds.Contains(r.OwnerId)).ToListAsync(ct)) reference.IsDeleted = true;
        row.IsDeleted = true; row.UpdatedAt = now;

        var knowledge = await _db.KnowledgeItems.FirstOrDefaultAsync(k => k.Id == knowledgeId, ct);
        if (knowledge != null)
        {
            knowledge.IsDeleted = true;
            knowledge.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted sealed item {KnowledgeId} and {CommentCount} sealed comments without decrypting them.", knowledgeId, comments.Count);
        return SealedStoreResult.Success(knowledgeId, 200, 1);
    }

    public async Task<SealedStoreResult> CreateCommentAsync(Guid knowledgeId, SealedCommentWrite write, CancellationToken ct, string? expectedRevision = null)
    {
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, knowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        ArgumentNullException.ThrowIfNull(write);
        if (write.PayloadKeyReferences != null) return await WriteGraphCommentAsync(knowledgeId, write, true, expectedRevision, ct);
        var problem = ValidateComment(write.Comment);
        if (problem != null)
            return SealedStoreResult.Fail(400, problem);

        var knowledge = await _db.ShSealedKnowledge.FirstOrDefaultAsync(s => s.KnowledgeId == knowledgeId, ct);
        if (knowledge == null)
            return SealedStoreResult.Fail(404, "Parent sealed item not found.");

        var original = write.Comment.OriginalCommentId != Guid.Empty
            ? write.Comment.OriginalCommentId
            : (write.Comment.Id != Guid.Empty ? write.Comment.Id : Guid.Empty);
        if (original != Guid.Empty)
        {
            var duplicate = await _db.ShSealedComments
                .AnyAsync(c => c.OriginalCommentId == original && c.KnowledgeId == knowledgeId && c.LineageScope == "", ct);
            if (duplicate)
                return SealedStoreResult.Fail(409, "Sealed comment with this original id already exists.");
        }

        var storageId = Guid.NewGuid();
        if (original == Guid.Empty)
            original = storageId;

        var row = new ShSealedComment
        {
            Id = storageId,
            TenantId = _tenant.TenantId,
            KnowledgeId = knowledgeId,
            OriginalCommentId = original,
            OriginalKnowledgeId = write.Comment.OriginalKnowledgeId != Guid.Empty ? write.Comment.OriginalKnowledgeId : knowledgeId,
            ParentCommentId = write.Comment.ParentCommentId,
            OriginalParentCommentId = write.Comment.OriginalParentCommentId,
            CreatedAt = DateTime.UtcNow,
        };
        ApplyCommentEnvelope(row, write.Comment);
        _db.ShSealedComments.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created sealed comment {CommentId} for sealed item {KnowledgeId}.",
            storageId, knowledgeId);
        return SealedStoreResult.Success(storageId, 201, 1);
    }

    public async Task<SealedCommentResponse?> GetCommentAsync(Guid commentId, CancellationToken ct)
    {
        var row = await _db.ShSealedComments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        return row == null ? null : ToCommentResponse(row);
    }

    public async Task<List<SealedCommentResponse>> ListCommentsAsync(Guid knowledgeId, CancellationToken ct)
    {
        var rows = await _db.ShSealedComments.AsNoTracking()
            .Where(c => c.KnowledgeId == knowledgeId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToCommentResponse).ToList();
    }

    public async Task<SealedStoreResult> UpdateCommentAsync(Guid commentId, SealedCommentWrite write, CancellationToken ct, string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        var currentGraph = await _db.ShSealedComments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == commentId && c.LineageScope != "", ct);
        if (currentGraph != null || write.PayloadKeyReferences != null) { write.Comment.Id = commentId; return await WriteGraphCommentAsync(currentGraph?.KnowledgeId ?? write.Comment.KnowledgeId, write, false, expectedRevision, ct); }
        var problem = ValidateComment(write.Comment);
        if (problem != null)
            return SealedStoreResult.Fail(400, problem);

        var row = await _db.ShSealedComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (row == null)
            return SealedStoreResult.Fail(404, "Sealed comment not found.");
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, row.KnowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);
        if (write.Comment.OriginalCommentId != Guid.Empty && write.Comment.OriginalCommentId != row.OriginalCommentId)
            return SealedStoreResult.Fail(400, "Original comment id cannot change.");

        ApplyCommentEnvelope(row, write.Comment);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated sealed comment {CommentId} for tenant {TenantId}.",
            commentId, _tenant.TenantId);
        return SealedStoreResult.Success(commentId, 200, 1);
    }

    public async Task<SealedStoreResult> DeleteCommentAsync(Guid commentId, CancellationToken ct, string? expectedRevision = null)
    {
        var row = await _db.ShSealedComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (row == null)
            return SealedStoreResult.Fail(404, "Sealed comment not found.");
        if (await SelfHostedInactiveShelf.IsInactiveAsync(_db, row.KnowledgeId, ct)) return SealedStoreResult.Fail(409, SelfHostedInactiveShelf.ReadOnlyMessage);

        if ((row.LineageScope.Length > 0 || expectedRevision != null) && expectedRevision?.Trim('"') != PayloadRevision(ToComment(row))) return SealedStoreResult.Fail(409, "Comment changed; reload before deleting.");
        var family = await _db.ShSealedComments.Where(c => c.KnowledgeId == row.KnowledgeId).ToListAsync(ct);
        var deletedIds = new HashSet<Guid> { commentId };
        while (true)
        {
            var count = deletedIds.Count;
            foreach (var child in family.Where(c => c.ParentCommentId.HasValue && deletedIds.Contains(c.ParentCommentId.Value))) deletedIds.Add(child.Id);
            if (count == deletedIds.Count) break;
        }
        foreach (var comment in family.Where(c => deletedIds.Contains(c.Id))) { comment.IsDeleted = true; comment.UpdatedAt = DateTime.UtcNow; }
        var links = await _db.ShSealedAttachments.Where(a => a.CommentId.HasValue && deletedIds.Contains(a.CommentId.Value)).ToListAsync(ct);
        await HideAttachmentsAndRetainFilesAsync(links, null, ct);
        var linkIds = links.Select(a => a.Id).ToArray();
        foreach (var reference in await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "comment" && deletedIds.Contains(r.OwnerId)
            || r.OwnerKind == "attachment" && linkIds.Contains(r.OwnerId)).ToListAsync(ct)) reference.IsDeleted = true;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted sealed comment {CommentId} without decrypting it.", commentId);
        return SealedStoreResult.Success(commentId, 200, 1);
    }

    public async Task<PortableExportPackage?> ExportVaultAsync(Guid vaultId, CancellationToken ct)
    {
        var vault = await _db.Vaults.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vaultId, ct);
        if (vault == null)
            return null;

        var knowledgeIds = await _db.KnowledgeVaults.AsNoTracking()
            .Where(link => link.VaultId == vaultId)
            .Select(link => link.KnowledgeId)
            .ToListAsync(ct);
        if (await _db.KnowledgeItems.AnyAsync(k => knowledgeIds.Contains(k.Id) && k.PortableRecoveryState != null, ct)) throw new InvalidDataException("Inactive recovery requires the complete archive export.");
        var sealedRows = await _db.ShSealedKnowledge.AsNoTracking()
            .Where(row => knowledgeIds.Contains(row.KnowledgeId))
            .ToListAsync(ct);
        var sealedIds = sealedRows.Select(row => row.KnowledgeId).ToList();
        var titles = await _db.KnowledgeItems.AsNoTracking()
            .Where(item => sealedIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Title, item.CreatedAt, item.UpdatedAt })
            .ToListAsync(ct);
        var contextIds = await _db.ShVaultCryptoContextLinks.AsNoTracking()
            .Where(link => link.DestinationVaultId == vaultId)
            .Select(link => link.PortableCryptoContextId)
            .ToListAsync(ct);
        var contexts = await _db.ShPortableCryptoContexts.AsNoTracking()
            .Where(context => contextIds.Contains(context.Id))
            .ToListAsync(ct);
        var keys = await _db.ShCryptoKeyIdentities.AsNoTracking()
            .Where(key => contextIds.Contains(key.PortableCryptoContextId))
            .ToListAsync(ct);
        var versions = await _db.ShSealedVersions.AsNoTracking()
            .Where(version => sealedIds.Contains(version.KnowledgeId))
            .ToListAsync(ct);
        var files = await _db.ShSealedFiles.AsNoTracking()
            .Where(file => file.KnowledgeId.HasValue && sealedIds.Contains(file.KnowledgeId.Value))
            .ToListAsync(ct);
        var comments = await _db.ShSealedComments.AsNoTracking()
            .Where(comment => sealedIds.Contains(comment.KnowledgeId))
            .ToListAsync(ct);

        var omissions = new List<PortableOmission>();
        if (keys.Count == 0)
        {
            omissions.Add(new PortableOmission
            {
                OwnerKind = "vault",
                OwnerId = vaultId,
                Reason = EncryptedPortabilityProfile.ReasonCatalog,
            });
        }

        return new PortableExportPackage
        {
            SchemaVersion = EncryptedPortabilityProfile.SchemaVersion,
            RequiredCapabilities = new List<string> { EncryptedPortabilityProfile.Capability },
            SourceEdition = "selfhosted",
            SourceTenantId = _tenant.TenantId,
            ExportedAt = DateTime.UtcNow,
            CryptoContexts = contexts.Select(context => new ExportedCryptoContext
            {
                ContextId = context.Id,
                BindingKind = context.BindingKind,
                OriginalBindingId = context.OriginalBindingId,
                AlgorithmProfile = context.AlgorithmProfile,
                ProvenanceStatus = context.ProvenanceStatus,
                SourceTenantId = context.SourceTenantId,
            }).ToList(),
            CryptoKeyCatalog = keys.Select(key => new ExportedCryptoKeyIdentity
            {
                ContextId = key.PortableCryptoContextId,
                Fingerprint = key.Fingerprint,
                PublicKey = key.PublicKey,
                KeyType = key.KeyType,
                Generation = key.Generation,
                Lifecycle = key.Lifecycle,
                Custody = key.Custody,
            }).ToList(),
            SealedItems = sealedRows.Select(ToItem).ToList(),
            SealedVersions = versions.Select(version => new SealedKnowledgeVersionRecord
            {
                Id = version.Id,
                KnowledgeId = version.KnowledgeId,
                OriginalKnowledgeId = version.OriginalKnowledgeId,
                VersionNumber = version.VersionNumber,
                Ciphertext = version.Ciphertext,
                EnvelopeMetadata = version.EnvelopeMetadata,
                CreatedAt = version.CreatedAt,
                UpdatedAt = version.UpdatedAt,
            }).ToList(),
            SealedFiles = files.Select(ToFile).ToList(),
            SealedComments = comments.Select(ToComment).ToList(),
            Omissions = omissions,
            Data = new PortableExportData
            {
                Vaults = new List<PortableVault>
                {
                    new()
                    {
                        Id = vault.Id,
                        Name = vault.Name,
                        CreatedAt = vault.CreatedAt,
                        UpdatedAt = vault.UpdatedAt,
                    }
                },
                KnowledgeItems = titles.Select(item => new PortableKnowledge
                {
                    Id = item.Id,
                    Title = item.Title,
                    Content = string.Empty,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    VaultIds = new List<Guid> { vaultId },
                    PrimaryVaultId = vaultId,
                }).ToList(),
            },
        };
    }

    private async Task<SealedStoreResult> ImportPackageAsync(
        PortableExportPackage package,
        Guid? destinationVaultId,
        string? label,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!EncryptedPortabilityProfile.IsSealedSchema(package.SchemaVersion))
            return SealedStoreResult.Fail(400, "Plaintext packages stay on the plaintext import path.");

        if (!destinationVaultId.HasValue || destinationVaultId.Value == Guid.Empty)
            return SealedStoreResult.Fail(400, "Destination vault is required.");

        var rejected = EncryptedPortabilityProfile.RejectReason(package)
            ?? EncryptedPortabilityProfile.ValidateSealedGraph(package);
        if (rejected != null)
            return SealedStoreResult.Fail(400, rejected);

        if (!await _db.Vaults.AnyAsync(v => v.Id == destinationVaultId.Value, ct))
            return SealedStoreResult.Fail(400, "Destination vault was not found.");

        foreach (var context in package.CryptoContexts ?? new List<ExportedCryptoContext>())
        {
            if (NormalizeBinding(context.BindingKind) == null)
                return SealedStoreResult.Fail(400, "Unsupported crypto binding kind.");
        }

        foreach (var key in package.CryptoKeyCatalog ?? new List<ExportedCryptoKeyIdentity>())
        {
            if (string.IsNullOrWhiteSpace(key.Fingerprint) || string.IsNullOrWhiteSpace(key.PublicKey))
                return SealedStoreResult.Fail(400, "Public key catalog entry is incomplete.");
            if (key.Fingerprint.Length > 128 || key.ContextId == Guid.Empty)
                return SealedStoreResult.Fail(400, "Public key catalog entry is incomplete.");
        }

        var items = package.SealedItems ?? new List<SealedKnowledgeItem>();
        foreach (var item in items)
        {
            var problem = ValidateItem(item);
            if (problem != null)
                return SealedStoreResult.Fail(400, problem);
            if (item.OriginalItemId == Guid.Empty && item.Id == Guid.Empty)
                return SealedStoreResult.Fail(400, "Sealed item is missing its original id.");
        }

        foreach (var file in package.SealedFiles ?? new List<SealedFileRecord>())
        {
            if (!EncryptedPortabilityProfile.IsCompleteFile(file))
                return SealedStoreResult.Fail(400, "Sealed file is incomplete; write refused.");
            if ((file.BinaryContentBase64?.Length ?? 0) > MaxEnvelopeChars)
                return SealedStoreResult.Fail(400, "Sealed file exceeds the stored envelope limit.");
        }

        var tenantId = _tenant.TenantId;
        var idMap = new Dictionary<Guid, Guid>();
        var created = 0;

        foreach (var source in package.CryptoContexts ?? new List<ExportedCryptoContext>())
        {
            var stored = await UpsertContextAsync(source, tenantId, ct);
            if (stored.Error != null)
                return SealedStoreResult.Fail(stored.Status, stored.Error);
            if (source.OriginalBindingId != Guid.Empty)
                idMap[source.OriginalBindingId] = stored.Id;
            await EnsureLinkAsync(destinationVaultId.Value, stored.Id, tenantId, ct);
        }

        foreach (var key in package.CryptoKeyCatalog ?? new List<ExportedCryptoKeyIdentity>())
        {
            var tracked = _db.ShPortableCryptoContexts.Local.FirstOrDefault(c => c.Id == key.ContextId);
            var ownerTenant = tracked?.TenantId;
            if (ownerTenant == null)
            {
                ownerTenant = await _db.ShPortableCryptoContexts.IgnoreQueryFilters()
                    .Where(c => c.Id == key.ContextId)
                    .Select(c => (Guid?)c.TenantId)
                    .FirstOrDefaultAsync(ct);
            }
            if (ownerTenant == null)
                return SealedStoreResult.Fail(400, "Public key references a context that was not imported.");
            if (ownerTenant != tenantId)
                return SealedStoreResult.Fail(403, "Public key context belongs to another tenant.");

            var existing = await _db.ShCryptoKeyIdentities
                .FirstOrDefaultAsync(k => k.PortableCryptoContextId == key.ContextId && k.Fingerprint == key.Fingerprint, ct);
            if (existing == null)
            {
                _db.ShCryptoKeyIdentities.Add(new ShCryptoKeyIdentity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PortableCryptoContextId = key.ContextId,
                    Fingerprint = key.Fingerprint,
                    PublicKey = key.PublicKey,
                    KeyType = string.IsNullOrWhiteSpace(key.KeyType) ? "x25519" : Trim(key.KeyType, 32),
                    Generation = key.Generation < 1 ? 1 : key.Generation,
                    Lifecycle = "decrypt-only",
                    Custody = "customer-held-public-only",
                });
            }
            else
            {
                existing.PublicKey = key.PublicKey;
                existing.Generation = key.Generation < 1 ? 1 : key.Generation;
                existing.Lifecycle = "decrypt-only";
                existing.Custody = "customer-held-public-only";
            }
        }

        Guid? singleId = null;
        foreach (var item in items)
        {
            var outcome = await UpsertItemAsync(item, destinationVaultId, label, tenantId, ct);
            if (outcome.Error != null)
                return SealedStoreResult.Fail(outcome.Status, outcome.Error);
            if (outcome.Created)
                created++;
            singleId = outcome.Id;
            idMap[outcome.Id] = outcome.Id;
            var original = item.OriginalItemId != Guid.Empty ? item.OriginalItemId : item.Id;
            idMap[original] = outcome.Id;
            if (item.Id != Guid.Empty)
                idMap[item.Id] = outcome.Id;
            if (item.CryptoBindingId != Guid.Empty && !idMap.ContainsKey(item.CryptoBindingId))
            {
                var context = await UpsertContextAsync(new ExportedCryptoContext
                {
                    BindingKind = "vault",
                    OriginalBindingId = item.CryptoBindingId,
                    AlgorithmProfile = item.AlgorithmProfile,
                    ProvenanceStatus = "archive-unverified",
                }, tenantId, ct);
                if (context.Error != null)
                    return SealedStoreResult.Fail(context.Status, context.Error);
                await EnsureLinkAsync(destinationVaultId.Value, context.Id, tenantId, ct);
            }
        }

        foreach (var version in package.SealedVersions ?? new List<SealedKnowledgeVersionRecord>())
        {
            if (!TryResolve(idMap, version.KnowledgeId, out var storage)
                && !TryResolve(idMap, version.OriginalKnowledgeId, out storage))
            {
                var known = await _db.ShSealedKnowledge.AsNoTracking()
                    .Where(s => s.LineageScope == "" && _db.KnowledgeVaults.Any(v => v.KnowledgeId == s.KnowledgeId && v.VaultId == destinationVaultId))
                    .Where(s => s.KnowledgeId == version.KnowledgeId || s.OriginalItemId == version.KnowledgeId || s.OriginalItemId == version.OriginalKnowledgeId).Take(2).ToListAsync(ct);
                if (known.Count != 1)
                    return SealedStoreResult.Fail(400, "Sealed version references an item that is not in the package.");
                storage = known[0].KnowledgeId;
            }

            if (string.IsNullOrWhiteSpace(version.EnvelopeMetadata)
                || !version.EnvelopeMetadata.Contains("\"sealedSnapshot\":true", StringComparison.Ordinal))
            {
                return SealedStoreResult.Fail(400, "Sealed version is incomplete; write refused.");
            }

            var owned = await FindOwnedVersionAsync(version.Id, tenantId, storage, ct);
            if (owned.Error != null)
                return SealedStoreResult.Fail(owned.Status, owned.Error);
            if (owned.Existing == null)
            {
                _db.ShSealedVersions.Add(new ShSealedVersion
                {
                    Id = version.Id != Guid.Empty ? version.Id : Guid.NewGuid(),
                    TenantId = tenantId,
                    KnowledgeId = storage,
                    OriginalKnowledgeId = version.OriginalKnowledgeId == Guid.Empty ? storage : version.OriginalKnowledgeId,
                    VersionNumber = version.VersionNumber,
                    Ciphertext = version.Ciphertext,
                    EnvelopeMetadata = version.EnvelopeMetadata,
                    CreatedAt = version.CreatedAt == default ? DateTime.UtcNow : version.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                owned.Existing.Ciphertext = version.Ciphertext;
                owned.Existing.EnvelopeMetadata = version.EnvelopeMetadata;
                owned.Existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        foreach (var file in package.SealedFiles ?? new List<SealedFileRecord>())
        {
            Guid? knowledgeId = null;
            if (file.KnowledgeId.HasValue)
            {
                if (!TryResolve(idMap, file.KnowledgeId.Value, out var storage))
                {
                    var known = await _db.ShSealedKnowledge.AsNoTracking()
                        .Where(s => s.LineageScope == "" && s.CryptoBindingId == file.CryptoBindingId && _db.KnowledgeVaults.Any(v => v.KnowledgeId == s.KnowledgeId && v.VaultId == destinationVaultId))
                        .Where(s => s.KnowledgeId == file.KnowledgeId || s.OriginalItemId == file.KnowledgeId).Take(2).ToListAsync(ct);
                    if (known.Count != 1)
                        return SealedStoreResult.Fail(400, "Sealed file references an item that is not in the package.");
                    storage = known[0].KnowledgeId;
                }
                knowledgeId = storage;
            }

            var owned = await FindOwnedFileAsync(file.Id, tenantId, knowledgeId, ct);
            if (owned.Error != null)
                return SealedStoreResult.Fail(owned.Status, owned.Error);
            if (owned.Existing == null)
            {
                _db.ShSealedFiles.Add(new ShSealedFile
                {
                    Id = file.Id != Guid.Empty ? file.Id : Guid.NewGuid(),
                    TenantId = tenantId,
                    KnowledgeId = knowledgeId,
                    ContentType = file.ContentType,
                    SizeBytes = file.SizeBytes,
                    OriginalSizeBytes = file.OriginalSizeBytes,
                    BinaryContentBase64 = file.BinaryContentBase64,
                    EncryptionKeyRef = file.EncryptionKeyRef,
                    EnvelopeVersion = file.EnvelopeVersion,
                    EncryptedDek = file.EncryptedDek,
                    EncryptedDekNonce = file.EncryptedDekNonce,
                    EphemeralPublicKey = file.EphemeralPublicKey,
                    ContentNonce = file.ContentNonce,
                    EncryptedFieldsJson = file.EncryptedFieldsJson,
                    CryptoBindingId = file.CryptoBindingId,
                    CreatedAt = file.CreatedAt == default ? DateTime.UtcNow : file.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                owned.Existing.BinaryContentBase64 = file.BinaryContentBase64;
                owned.Existing.EncryptedDek = file.EncryptedDek;
                owned.Existing.EncryptedDekNonce = file.EncryptedDekNonce;
                owned.Existing.EphemeralPublicKey = file.EphemeralPublicKey;
                owned.Existing.ContentNonce = file.ContentNonce;
                owned.Existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        foreach (var comment in package.SealedComments ?? new List<SealedCommentRecord>())
        {
            if (!EncryptedPortabilityProfile.IsCompleteComment(comment))
                return SealedStoreResult.Fail(400, "Sealed comment is incomplete; write refused.");
            if ((comment.Ciphertext?.Length ?? 0) > MaxEnvelopeChars)
                return SealedStoreResult.Fail(400, "Sealed comment exceeds the stored envelope limit.");

            Guid? knowledgeId = null;
            if (!TryResolve(idMap, comment.KnowledgeId, out var storage)
                && !TryResolve(idMap, comment.OriginalKnowledgeId, out storage))
            {
                var known = await _db.ShSealedKnowledge.AsNoTracking()
                    .Where(s => s.LineageScope == "" && s.CryptoBindingId == comment.CryptoBindingId && _db.KnowledgeVaults.Any(v => v.KnowledgeId == s.KnowledgeId && v.VaultId == destinationVaultId))
                    .Where(s => s.KnowledgeId == comment.KnowledgeId || s.OriginalItemId == comment.KnowledgeId || s.OriginalItemId == comment.OriginalKnowledgeId).Take(2).ToListAsync(ct);
                if (known.Count != 1)
                    return SealedStoreResult.Fail(400, "Sealed comment references an item that is not in the package.");
                storage = known[0].KnowledgeId;
            }
            knowledgeId = storage;

            Guid? parentCommentId = null;
            if (comment.ParentCommentId.HasValue && comment.ParentCommentId.Value != Guid.Empty)
            {
                if (TryResolve(idMap, comment.ParentCommentId.Value, out var parentStorage)
                    || (comment.OriginalParentCommentId.HasValue
                        && TryResolve(idMap, comment.OriginalParentCommentId.Value, out parentStorage)))
                {
                    parentCommentId = parentStorage;
                }
                else
                {
                    var parents = await _db.ShSealedComments.AsNoTracking().Where(c => c.LineageScope == "" && c.KnowledgeId == storage
                        && (c.Id == comment.ParentCommentId || (comment.OriginalParentCommentId.HasValue && c.OriginalCommentId == comment.OriginalParentCommentId))).Take(2).ToListAsync(ct);
                    parents.AddRange(_db.ShSealedComments.Local.Where(c => c.LineageScope == "" && c.KnowledgeId == storage
                        && (c.Id == comment.ParentCommentId || (comment.OriginalParentCommentId.HasValue && c.OriginalCommentId == comment.OriginalParentCommentId)) && parents.All(p => p.Id != c.Id)));
                    if (parents.Count != 1) return SealedStoreResult.Fail(409, "Legacy parent comment is missing or ambiguous.");
                    parentCommentId = parents[0].Id;
                }
            }

            var original = comment.OriginalCommentId != Guid.Empty ? comment.OriginalCommentId : comment.Id;
            var owned = await FindOwnedCommentAsync(comment.Id, original, tenantId, knowledgeId, ct);
            if (owned.Error != null)
                return SealedStoreResult.Fail(owned.Status, owned.Error);
            if (owned.Existing == null)
            {
                var storageCommentId = comment.Id != Guid.Empty ? comment.Id : Guid.NewGuid();
                _db.ShSealedComments.Add(new ShSealedComment
                {
                    Id = storageCommentId,
                    TenantId = tenantId,
                    KnowledgeId = knowledgeId!.Value,
                    OriginalCommentId = original == Guid.Empty ? storageCommentId : original,
                    OriginalKnowledgeId = comment.OriginalKnowledgeId == Guid.Empty ? knowledgeId!.Value : comment.OriginalKnowledgeId,
                    ParentCommentId = parentCommentId,
                    OriginalParentCommentId = comment.OriginalParentCommentId,
                    Ciphertext = comment.Ciphertext,
                    EnvelopeVersion = comment.EnvelopeVersion,
                    EncryptedDek = comment.EncryptedDek,
                    EncryptedDekNonce = comment.EncryptedDekNonce,
                    EphemeralPublicKey = comment.EphemeralPublicKey,
                    ContentNonce = comment.ContentNonce,
                    EncryptionKeyRef = comment.EncryptionKeyRef,
                    EncryptedFieldsJson = comment.EncryptedFieldsJson,
                    CryptoBindingId = comment.CryptoBindingId,
                    AlgorithmProfile = string.IsNullOrWhiteSpace(comment.AlgorithmProfile) ? "x25519-vault" : comment.AlgorithmProfile,
                    CreatedAt = comment.CreatedAt == default ? DateTime.UtcNow : comment.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                });
                if (original != Guid.Empty)
                    idMap[original] = storageCommentId;
                if (comment.Id != Guid.Empty)
                    idMap[comment.Id] = storageCommentId;
                idMap[storageCommentId] = storageCommentId;
            }
            else
            {
                owned.Existing.Ciphertext = comment.Ciphertext;
                owned.Existing.EncryptedDek = comment.EncryptedDek;
                owned.Existing.EncryptedDekNonce = comment.EncryptedDekNonce;
                owned.Existing.EphemeralPublicKey = comment.EphemeralPublicKey;
                owned.Existing.ContentNonce = comment.ContentNonce;
                owned.Existing.EncryptionKeyRef = comment.EncryptionKeyRef;
                owned.Existing.EncryptedFieldsJson = comment.EncryptedFieldsJson;
                owned.Existing.ParentCommentId = parentCommentId ?? owned.Existing.ParentCommentId;
                owned.Existing.UpdatedAt = DateTime.UtcNow;
                idMap[owned.Existing.OriginalCommentId] = owned.Existing.Id;
                idMap[owned.Existing.Id] = owned.Existing.Id;
                if (comment.Id != Guid.Empty)
                    idMap[comment.Id] = owned.Existing.Id;
            }
        }

        await _db.SaveChangesAsync(ct);
        var written = items.Count;
        _logger.LogInformation(
            "Stored {Count} sealed items for tenant {TenantId}. Server decrypt was not performed.",
            written, tenantId);
        var status = created > 0 ? 201 : 200;
        return SealedStoreResult.Success(written == 1 ? singleId : null, status, written);
    }

    private async Task<(Guid Id, bool Created, string? Error, int Status)> UpsertItemAsync(
        SealedKnowledgeItem item,
        Guid? vaultId,
        string? label,
        Guid tenantId,
        CancellationToken ct)
    {
        var original = item.OriginalItemId != Guid.Empty ? item.OriginalItemId : item.Id;
        var existing = item.Id == Guid.Empty ? null : await _db.ShSealedKnowledge.SingleOrDefaultAsync(s => s.KnowledgeId == item.Id, ct);
        if (existing != null && (existing.LineageScope.Length > 0 || !await _db.KnowledgeVaults.AnyAsync(v => v.KnowledgeId == existing.KnowledgeId && v.VaultId == vaultId, ct)))
            return (Guid.Empty, false, "Use the complete graph protocol for this destination item.", 409);
        if (existing == null)
        {
            var matches = await _db.ShSealedKnowledge.Where(s => s.TenantId == tenantId && s.LineageScope == ""
                && s.OriginalItemId == original && s.CryptoBindingId == item.CryptoBindingId
                && _db.KnowledgeVaults.Any(v => v.KnowledgeId == s.KnowledgeId && v.VaultId == vaultId)).Take(2).ToListAsync(ct);
            if (matches.Count > 1) return (Guid.Empty, false, "Legacy original item identity is ambiguous; use an explicit logical identity.", 409);
            existing = matches.SingleOrDefault();
        }

        if (existing == null)
        {
            var storageId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            _db.KnowledgeItems.Add(new Knowledge
            {
                Id = storageId,
                TenantId = tenantId,
                Title = LabelOrDefault(label),
                Content = string.Empty,
                Type = KnowledgeType.Note,
                Source = "sealed",
                CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
                UpdatedAt = now,
            });
            _db.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = tenantId,
                KnowledgeId = storageId,
                VaultId = vaultId!.Value,
                IsPrimary = true,
            });

            var row = new ShSealedKnowledge
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                KnowledgeId = storageId,
                OriginalItemId = original,
                CreatedAt = now,
            };
            ApplyEnvelope(row, item);
            _db.ShSealedKnowledge.Add(row);
            return (storageId, true, null, 201);
        }

        await SnapshotAsync(existing, ct);
        ApplyEnvelope(existing, item);
        var knowledge = await _db.KnowledgeItems.FirstOrDefaultAsync(k => k.Id == existing.KnowledgeId, ct);
        if (knowledge != null)
        {
            knowledge.Content = string.Empty;
            if (!string.IsNullOrWhiteSpace(label))
                knowledge.Title = LabelOrDefault(label);
            knowledge.UpdatedAt = DateTime.UtcNow;
        }

        return (existing.KnowledgeId, false, null, 200);
    }

    private async Task<(Guid Id, string? Error, int Status)> UpsertContextAsync(
        ExportedCryptoContext source,
        Guid tenantId,
        CancellationToken ct)
    {
        var binding = NormalizeBinding(source.BindingKind)!;
        ShPortableCryptoContext? existing = null;
        if (source.ContextId != Guid.Empty)
        {
            existing = await _db.ShPortableCryptoContexts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == source.ContextId, ct);
            if (existing != null && existing.TenantId != tenantId)
                return (Guid.Empty, "Crypto context id belongs to another tenant.", 403);
        }

        if (existing == null && source.ContextId == Guid.Empty)
        {
            var matches = await _db.ShPortableCryptoContexts.Where(c => c.OriginalBindingId == source.OriginalBindingId && c.BindingKind == binding
                && !_db.ShPayloadKeyReferences.Any(r => r.ContextId == c.Id)).Take(2).ToListAsync(ct);
            if (matches.Count > 1) return (Guid.Empty, "Legacy crypto context is ambiguous; use an explicit context identity.", 409);
            existing = matches.SingleOrDefault();
        }
        if (existing != null && await _db.ShPayloadKeyReferences.AnyAsync(r => r.ContextId == existing.Id, ct))
            return (Guid.Empty, "Use the complete graph protocol for this destination context.", 409);

        if (existing == null)
        {
            existing = new ShPortableCryptoContext
            {
                Id = source.ContextId != Guid.Empty ? source.ContextId : Guid.NewGuid(),
                TenantId = tenantId,
                BindingKind = binding,
                OriginalBindingId = source.OriginalBindingId,
                AlgorithmProfile = string.IsNullOrWhiteSpace(source.AlgorithmProfile) ? "x25519-vault" : Trim(source.AlgorithmProfile, 64),
                ProvenanceStatus = NormalizeProvenance(source.ProvenanceStatus),
                SourceTenantId = source.SourceTenantId,
            };
            _db.ShPortableCryptoContexts.Add(existing);
        }
        else
        {
            existing.AlgorithmProfile = string.IsNullOrWhiteSpace(source.AlgorithmProfile)
                ? existing.AlgorithmProfile
                : Trim(source.AlgorithmProfile, 64);
            existing.ProvenanceStatus = NormalizeProvenance(source.ProvenanceStatus);
            existing.UpdatedAt = DateTime.UtcNow;
        }

        return (existing.Id, null, 200);
    }

    private async Task<(ShSealedVersion? Existing, string? Error, int Status)> FindOwnedVersionAsync(
        Guid versionId,
        Guid tenantId,
        Guid storage,
        CancellationToken ct)
    {
        if (versionId == Guid.Empty)
            return (null, null, 200);

        var existing = await _db.ShSealedVersions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (existing == null)
            return (null, null, 200);
        if (existing.TenantId != tenantId)
            return (null, "Sealed version belongs to another tenant.", 403);
        if (existing.KnowledgeId != storage)
            return (null, "Sealed version does not belong to this item.", 409);
        return (existing, null, 200);
    }

    private async Task<(ShSealedFile? Existing, string? Error, int Status)> FindOwnedFileAsync(
        Guid fileId,
        Guid tenantId,
        Guid? knowledgeId,
        CancellationToken ct)
    {
        if (fileId == Guid.Empty)
            return (null, null, 200);

        var existing = await _db.ShSealedFiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (existing == null)
            return (null, null, 200);
        if (existing.TenantId != tenantId)
            return (null, "Sealed file belongs to another tenant.", 403);
        if (knowledgeId.HasValue && existing.KnowledgeId.HasValue && existing.KnowledgeId != knowledgeId)
            return (null, "Sealed file does not belong to this item.", 409);
        return (existing, null, 200);
    }

    private async Task<(ShSealedComment? Existing, string? Error, int Status)> FindOwnedCommentAsync(
        Guid commentId,
        Guid originalCommentId,
        Guid tenantId,
        Guid? knowledgeId,
        CancellationToken ct)
    {
        ShSealedComment? existing = null;
        if (commentId != Guid.Empty)
        {
            existing = _db.ShSealedComments.Local.FirstOrDefault(c => c.Id == commentId)
                ?? await _db.ShSealedComments.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == commentId, ct);
        }
        if (existing == null && originalCommentId != Guid.Empty)
        {
            var matches = await _db.ShSealedComments.Where(c => c.TenantId == tenantId && c.LineageScope == "" && c.KnowledgeId == knowledgeId && c.OriginalCommentId == originalCommentId).Take(2).ToListAsync(ct);
            matches.AddRange(_db.ShSealedComments.Local.Where(c => c.TenantId == tenantId && c.LineageScope == "" && c.KnowledgeId == knowledgeId && c.OriginalCommentId == originalCommentId && matches.All(m => m.Id != c.Id)));
            if (matches.Count > 1) return (null, "Legacy original comment identity is ambiguous; use an explicit logical identity.", 409);
            existing = matches.SingleOrDefault();
        }
        if (existing == null)
            return (null, null, 200);
        if (existing.TenantId != tenantId)
            return (null, "Sealed comment belongs to another tenant.", 403);
        if (existing.LineageScope.Length > 0) return (null, "Use the complete graph protocol for this destination comment.", 409);
        if (knowledgeId.HasValue && existing.KnowledgeId != knowledgeId)
            return (null, "Sealed comment does not belong to this item.", 409);
        return (existing, null, 200);
    }

    private async Task EnsureLinkAsync(Guid vaultId, Guid contextId, Guid tenantId, CancellationToken ct)
    {
        var link = await _db.ShVaultCryptoContextLinks
            .FirstOrDefaultAsync(l => l.DestinationVaultId == vaultId && l.PortableCryptoContextId == contextId, ct);
        if (link != null)
            return;

        _db.ShVaultCryptoContextLinks.Add(new ShVaultCryptoContextLink
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DestinationVaultId = vaultId,
            PortableCryptoContextId = contextId,
            IsPrimaryWriteContext = false,
        });
    }

    private async Task SnapshotAsync(ShSealedKnowledge row, CancellationToken ct)
    {
        var numbers = await _db.ShSealedVersions.Where(v => v.KnowledgeId == row.KnowledgeId)
            .Select(v => v.VersionNumber)
            .ToListAsync(ct);
        var next = numbers.Count == 0 ? 0 : numbers.Max();
        var snapshotId = Guid.NewGuid();
        _db.ShSealedVersions.Add(new ShSealedVersion
        {
            Id = snapshotId,
            TenantId = row.TenantId,
            KnowledgeId = row.KnowledgeId,
            OriginalKnowledgeId = row.OriginalItemId,
            VersionNumber = next + 1,
            Ciphertext = row.Ciphertext,
            EnvelopeMetadata = JsonSerializer.Serialize(new
            {
                sealedSnapshot = true,
                isEncrypted = true,
                envelopeVersion = row.EnvelopeVersion,
                encryptionEnvelopeVersion = row.EnvelopeVersion,
                encryptedDek = row.EncryptedDek,
                encryptedDekNonce = row.EncryptedDekNonce,
                ephemeralPublicKey = row.EphemeralPublicKey,
                contentNonce = row.ContentNonce,
                encryptionKeyRef = row.EncryptionKeyRef,
                encryptedFieldsJson = row.EncryptedFieldsJson,
                wrappedDek = row.WrappedDek,
                dekNonce = row.DekNonce,
                cryptoVersion = row.CryptoVersion,
                cryptoBindingId = row.CryptoBindingId,
                algorithmProfile = row.AlgorithmProfile,
            }, Camel),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var references = await _db.ShPayloadKeyReferences.Where(r => r.OwnerKind == "knowledge" && r.OwnerId == row.KnowledgeId).AsNoTracking().ToListAsync(ct);
        foreach (var reference in references)
        {
            reference.Id = Guid.NewGuid(); reference.OwnerKind = "version"; reference.OwnerId = snapshotId; reference.VersionId = snapshotId;
            _db.ShPayloadKeyReferences.Add(reference);
        }
    }

    private static void ApplyEnvelope(ShSealedKnowledge row, SealedKnowledgeItem item)
    {
        row.Ciphertext = item.Ciphertext;
        row.EnvelopeVersion = item.EnvelopeVersion;
        row.EncryptedDek = item.EncryptedDek;
        row.EncryptedDekNonce = item.EncryptedDekNonce;
        row.EphemeralPublicKey = item.EphemeralPublicKey;
        row.ContentNonce = item.ContentNonce;
        row.EncryptionKeyRef = item.EncryptionKeyRef;
        row.EncryptedFieldsJson = item.EncryptedFieldsJson;
        row.WrappedDek = item.WrappedDek;
        row.DekNonce = item.DekNonce;
        row.CryptoVersion = item.CryptoVersion;
        row.CryptoBindingId = item.CryptoBindingId;
        row.AlgorithmProfile = string.IsNullOrWhiteSpace(item.AlgorithmProfile) ? "x25519-vault" : Trim(item.AlgorithmProfile, 64);
        row.LifecycleStatus = LockedStatus;
        row.VerifiedFingerprint = null;
        row.ClientVerifiedAt = null;
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static string? ValidateItem(SealedKnowledgeItem item)
    {
        if (!EncryptedPortabilityProfile.IsCompleteItem(item))
            return "Sealed item is incomplete; write refused.";
        if (item.Ciphertext.Length > MaxEnvelopeChars || (item.EncryptedFieldsJson?.Length ?? 0) > MaxEnvelopeChars)
            return "Sealed item exceeds the stored envelope limit.";
        return null;
    }

    private static SealedItemResponse ToResponse(ShSealedKnowledge row) => new(
        row.KnowledgeId,
        row.OriginalItemId,
        row.LifecycleStatus,
        row.VerifiedFingerprint,
        row.ClientVerifiedAt,
        ToItem(row),
        ItemRevision(row));

    private static SealedKnowledgeItem ToItem(ShSealedKnowledge row) => new()
    {
        Id = row.KnowledgeId,
        OriginalItemId = row.OriginalItemId,
        Ciphertext = row.Ciphertext,
        EnvelopeVersion = row.EnvelopeVersion,
        EncryptedDek = row.EncryptedDek,
        EncryptedDekNonce = row.EncryptedDekNonce,
        EphemeralPublicKey = row.EphemeralPublicKey,
        ContentNonce = row.ContentNonce,
        EncryptionKeyRef = row.EncryptionKeyRef,
        EncryptedFieldsJson = row.EncryptedFieldsJson,
        WrappedDek = row.WrappedDek,
        DekNonce = row.DekNonce,
        CryptoVersion = row.CryptoVersion,
        CryptoBindingId = row.CryptoBindingId,
        AlgorithmProfile = row.AlgorithmProfile,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static SealedFileRecord ToFile(ShSealedFile file) => new()
    {
        Id = file.Id,
        KnowledgeId = file.KnowledgeId,
        ContentType = file.ContentType,
        SizeBytes = file.SizeBytes,
        OriginalSizeBytes = file.OriginalSizeBytes,
        BinaryContentBase64 = file.BinaryContentBase64,
        EncryptionKeyRef = file.EncryptionKeyRef,
        EnvelopeVersion = file.EnvelopeVersion,
        EncryptedDek = file.EncryptedDek,
        EncryptedDekNonce = file.EncryptedDekNonce,
        EphemeralPublicKey = file.EphemeralPublicKey,
        ContentNonce = file.ContentNonce,
        EncryptedFieldsJson = file.EncryptedFieldsJson,
        CryptoBindingId = file.CryptoBindingId,
        CreatedAt = file.CreatedAt,
        UpdatedAt = file.UpdatedAt,
    };

    private static bool TryResolve(Dictionary<Guid, Guid> map, Guid id, out Guid storage)
    {
        if (id != Guid.Empty && map.TryGetValue(id, out storage))
            return true;
        storage = Guid.Empty;
        return false;
    }

    private static string? NormalizeBinding(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind == "vault")
            return "vault";
        return kind == "release-lock" ? kind : null;
    }

    private static string NormalizeProvenance(string? status) =>
        status is "archive-unverified" or "client-asserted" ? status : "archive-unverified";

    private static string LabelOrDefault(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "Locked memory";
        var trimmed = label.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void ApplyCommentEnvelope(ShSealedComment row, SealedCommentRecord comment)
    {
        row.Ciphertext = comment.Ciphertext;
        row.EnvelopeVersion = comment.EnvelopeVersion;
        row.EncryptedDek = comment.EncryptedDek;
        row.EncryptedDekNonce = comment.EncryptedDekNonce;
        row.EphemeralPublicKey = comment.EphemeralPublicKey;
        row.ContentNonce = comment.ContentNonce;
        row.EncryptionKeyRef = comment.EncryptionKeyRef;
        row.EncryptedFieldsJson = comment.EncryptedFieldsJson;
        row.CryptoBindingId = comment.CryptoBindingId;
        row.AlgorithmProfile = string.IsNullOrWhiteSpace(comment.AlgorithmProfile) ? "x25519-vault" : Trim(comment.AlgorithmProfile, 64);
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static string? ValidateComment(SealedCommentRecord comment)
    {
        if (!EncryptedPortabilityProfile.IsCompleteComment(comment))
            return "Sealed comment is incomplete; write refused.";
        if (comment.Ciphertext.Length > MaxEnvelopeChars || (comment.EncryptedFieldsJson?.Length ?? 0) > MaxEnvelopeChars)
            return "Sealed comment exceeds the stored envelope limit.";
        return null;
    }

    private static SealedCommentRecord ToComment(ShSealedComment row) => new()
    {
        Id = row.Id,
        OriginalCommentId = row.OriginalCommentId,
        KnowledgeId = row.KnowledgeId,
        OriginalKnowledgeId = row.OriginalKnowledgeId,
        ParentCommentId = row.ParentCommentId,
        OriginalParentCommentId = row.OriginalParentCommentId,
        Ciphertext = row.Ciphertext,
        EnvelopeVersion = row.EnvelopeVersion,
        EncryptedDek = row.EncryptedDek,
        EncryptedDekNonce = row.EncryptedDekNonce,
        EphemeralPublicKey = row.EphemeralPublicKey,
        ContentNonce = row.ContentNonce,
        EncryptionKeyRef = row.EncryptionKeyRef,
        EncryptedFieldsJson = row.EncryptedFieldsJson,
        CryptoBindingId = row.CryptoBindingId,
        AlgorithmProfile = row.AlgorithmProfile,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static SealedCommentResponse ToCommentResponse(ShSealedComment row) => new(
        row.Id,
        row.OriginalCommentId,
        row.KnowledgeId,
        row.ParentCommentId,
        ToComment(row));
}
