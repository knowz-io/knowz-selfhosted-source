namespace Knowz.SelfHosted.Application.Services;

using System.IO.Compression;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class PortableExportService : IPortableExportService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IFileStorageProvider _storageProvider;
    private readonly SelfHostedOptions _options;
    private readonly ILogger<PortableExportService> _logger;

    public PortableExportService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IFileStorageProvider storageProvider,
        IOptions<SelfHostedOptions> options,
        ILogger<PortableExportService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _storageProvider = storageProvider;
        _options = options.Value;
        _logger = logger;
    }

    public Task<PortableExportPackage> ExportAsync(CancellationToken ct = default)
        => ExportAsync(new ExportScope { Type = ExportScopeType.Tenant }, ct);

    public Task<PortableExportPackage> ExportAsync(ExportScope scope, CancellationToken ct = default) => ExportMetadataAsync(scope, false, ct);

    internal async Task<PortableExportPackage> ExportMetadataAsync(ExportScope scope, bool includeSealed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateScope(scope);
        var scoped = scope.Type != ExportScopeType.Tenant;
        var tenantId = _tenantProvider.TenantId;
        var selectedVaultIds = (scope.VaultIds ?? []).ToHashSet();
        var selectedItemIds = (scope.KnowledgeItemIds ?? []).ToHashSet();
        var vaultScope = scope.Type is ExportScopeType.Vault or ExportScopeType.VaultWithChildren;

        _logger.LogInformation("Starting portable export for tenant {TenantId}, scope {ScopeType}", tenantId, scope.Type);

        // The same tenant/soft-delete filters and DTO projections serve whole-tenant and scoped export.
        var vaults = await _db.Vaults.Include(v => v.VaultPersons).AsNoTracking().ToListAsync(ct);
        if (vaultScope)
        {
            if (selectedVaultIds.Except(vaults.Select(v => v.Id)).Any())
                throw new ArgumentException("One or more selected vaults are unavailable in this tenant.");
            if (scope.Type == ExportScopeType.VaultWithChildren)
            {
                bool changed;
                do
                {
                    changed = false;
                    foreach (var vault in vaults.Where(v => v.ParentVaultId.HasValue && selectedVaultIds.Contains(v.ParentVaultId.Value)))
                        changed |= selectedVaultIds.Add(vault.Id);
                } while (changed);
            }
        }

        var knowledgeQuery = _db.KnowledgeItems.AsQueryable();
        if (vaultScope)
            knowledgeQuery = knowledgeQuery.Where(k => k.KnowledgeVaults.Any(kv => kv.TenantId == tenantId && selectedVaultIds.Contains(kv.VaultId)));
        else if (scoped)
            knowledgeQuery = knowledgeQuery.Where(k => selectedItemIds.Contains(k.Id));
        var knowledge = await knowledgeQuery
            .Include(k => k.KnowledgeVaults).Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations).Include(k => k.KnowledgeEvents).Include(k => k.Tags)
            .AsNoTracking().ToListAsync(ct);
        if (scoped && !vaultScope && selectedItemIds.Except(knowledge.Select(k => k.Id)).Any())
            throw new ArgumentException("One or more selected knowledge items are unavailable in this tenant.");

        var knowledgeIds = knowledge.Select(k => k.Id).ToHashSet();
        if (!includeSealed && (knowledge.Any(k => k.PortableRecoveryState != null || !string.IsNullOrWhiteSpace(k.EncryptedFieldsJson)) || await _db.ShSealedKnowledge.AnyAsync(row => knowledgeIds.Contains(row.KnowledgeId), ct)))
            throw new SealedItemLockedException();
        if (scoped)
        {
            if (!vaultScope)
                selectedVaultIds = knowledge.SelectMany(k => k.KnowledgeVaults).Where(kv => kv.TenantId == tenantId).Select(kv => kv.VaultId).ToHashSet();
            vaults = vaults.Where(v => selectedVaultIds.Contains(v.Id)).ToList();
        }
        var vaultIds = vaults.Select(v => v.Id).ToHashSet();
        var topicIds = knowledge.Where(k => k.TopicId.HasValue).Select(k => k.TopicId!.Value).ToHashSet();
        var tagIds = knowledge.SelectMany(k => k.Tags).Select(t => t.Id).ToHashSet();
        var personIds = knowledge.SelectMany(k => k.KnowledgePersons).Select(p => p.PersonId)
            .Concat(vaults.SelectMany(v => v.VaultPersons).Select(p => p.PersonId)).ToHashSet();
        var locationIds = knowledge.SelectMany(k => k.KnowledgeLocations).Select(l => l.LocationId).ToHashSet();
        var eventIds = knowledge.SelectMany(k => k.KnowledgeEvents).Select(e => e.EventId).ToHashSet();
        var topics = await _db.Topics.Where(t => !scoped || topicIds.Contains(t.Id)).AsNoTracking().ToListAsync(ct);
        var tags = await _db.Tags.Where(t => !scoped || tagIds.Contains(t.Id)).AsNoTracking().ToListAsync(ct);
        var persons = await _db.Persons.Where(p => !scoped || personIds.Contains(p.Id)).AsNoTracking().ToListAsync(ct);
        var locations = await _db.Locations.Where(l => !scoped || locationIds.Contains(l.Id)).AsNoTracking().ToListAsync(ct);
        var events = await _db.Events.Where(e => !scoped || eventIds.Contains(e.Id)).AsNoTracking().ToListAsync(ct);
        var inboxItems = await _db.InboxItems.Where(i => !scoped).AsNoTracking().ToListAsync(ct);
        var comments = await _db.Comments.Where(c => !scoped || knowledgeIds.Contains(c.KnowledgeId)).AsNoTracking().ToListAsync(ct);
        var commentIds = comments.Select(c => c.Id).ToHashSet();
        var encryptedCommentIds = await _db.ShSealedComments.Where(c => !scoped || knowledgeIds.Contains(c.KnowledgeId)).Select(c => c.Id).ToListAsync(ct);
        var allCommentIds = commentIds.Concat(encryptedCommentIds).ToArray();
        var graphFiles = _db.ShSealedAttachments.Where(a => !scoped ||
            a.KnowledgeId.HasValue && knowledgeIds.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && allCommentIds.Contains(a.CommentId.Value)).Select(a => a.FileRecordId);
        if (!includeSealed && (encryptedCommentIds.Count > 0 || await graphFiles.AnyAsync(ct) || await _db.ShSealedFiles.AnyAsync(f => !scoped || f.KnowledgeId.HasValue && knowledgeIds.Contains(f.KnowledgeId.Value), ct)))
            throw new SealedItemLockedException();
        var fileRecords = await _db.FileRecords
            .Where(f => !scoped || graphFiles.Contains(f.Id) || f.Attachments.Any(a => a.TenantId == tenantId &&
                (a.KnowledgeId.HasValue && knowledgeIds.Contains(a.KnowledgeId.Value) ||
                 a.CommentId.HasValue && commentIds.Contains(a.CommentId.Value))))
            .Include(f => f.Attachments).AsNoTracking().ToListAsync(ct);
        var pendingAncestors = fileRecords.Where(f => f.PreviousFileRecordId.HasValue).Select(f => f.PreviousFileRecordId!.Value).Except(fileRecords.Select(f => f.Id)).ToList();
        while (pendingAncestors.Count > 0)
        {
            var ancestors = await _db.FileRecords.Where(f => pendingAncestors.Contains(f.Id)).Include(f => f.Attachments).AsNoTracking().ToListAsync(ct);
            fileRecords.AddRange(ancestors);
            pendingAncestors = ancestors.Where(f => f.PreviousFileRecordId.HasValue).Select(f => f.PreviousFileRecordId!.Value).Except(fileRecords.Select(f => f.Id)).ToList();
        }
        if (!includeSealed && fileRecords.Any(f => f.EncryptedFieldsJson != null)) throw new SealedItemLockedException();
        // Opaque archives/inbox have no reliable vault/item ownership: never include them in a scoped package.
        var archives = await _db.PortableArchives.Where(a => !scoped && a.TenantId == tenantId).AsNoTracking().ToListAsync(ct);

        if (scoped)
        {
            topicIds = topics.Select(t => t.Id).ToHashSet();
            tagIds = tags.Select(t => t.Id).ToHashSet();
            personIds = persons.Select(p => p.Id).ToHashSet();
            locationIds = locations.Select(l => l.Id).ToHashSet();
            eventIds = events.Select(e => e.Id).ToHashSet();
            foreach (var item in knowledge)
            {
                item.KnowledgeVaults = item.KnowledgeVaults.Where(kv => kv.TenantId == tenantId && vaultIds.Contains(kv.VaultId)).ToList();
                item.KnowledgePersons = item.KnowledgePersons.Where(p => personIds.Contains(p.PersonId)).ToList();
                item.KnowledgeLocations = item.KnowledgeLocations.Where(l => locationIds.Contains(l.LocationId)).ToList();
                item.KnowledgeEvents = item.KnowledgeEvents.Where(e => eventIds.Contains(e.EventId)).ToList();
                item.Tags = item.Tags.Where(t => tagIds.Contains(t.Id)).ToList();
                if (item.TopicId.HasValue && !topicIds.Contains(item.TopicId.Value)) item.TopicId = null;
            }
            foreach (var vault in vaults)
            {
                if (vault.ParentVaultId.HasValue && !vaultIds.Contains(vault.ParentVaultId.Value)) vault.ParentVaultId = null;
                vault.VaultPersons = vault.VaultPersons.Where(p => personIds.Contains(p.PersonId)).ToList();
            }
            foreach (var comment in comments)
                if (comment.ParentCommentId.HasValue && !commentIds.Contains(comment.ParentCommentId.Value)) comment.ParentCommentId = null;
            foreach (var file in fileRecords)
            {
                // A shared binary is included once; its other attachment owners are not exposed.
                file.Attachments = file.Attachments.Where(a => a.TenantId == tenantId &&
                    (a.KnowledgeId.HasValue || a.CommentId.HasValue) &&
                    (!a.KnowledgeId.HasValue || knowledgeIds.Contains(a.KnowledgeId.Value)) &&
                    (!a.CommentId.HasValue || commentIds.Contains(a.CommentId.Value))).ToList();
            }
            var graphFileIds = (await graphFiles.ToListAsync(ct)).ToHashSet();
            var allowedFileIds = fileRecords.Where(f => f.Attachments.Count > 0 || graphFileIds.Contains(f.Id)).Select(f => f.Id).ToHashSet();
            var byId = fileRecords.ToDictionary(f => f.Id);
            foreach (var id in allowedFileIds.ToArray())
            {
                var cursor = byId[id];
                while (cursor.PreviousFileRecordId is Guid previous && byId.TryGetValue(previous, out var ancestor) && allowedFileIds.Add(previous)) cursor = ancestor;
            }
            fileRecords = fileRecords.Where(f => allowedFileIds.Contains(f.Id)).ToList();
        }

        // Map to portable DTOs
        var portableVaults = vaults.Select(v =>
        {
            var dto = new PortableVault
            {
                Id = v.Id,
                Name = v.Name,
                Description = v.Description,
                VaultType = v.VaultType,
                IsDefault = v.IsDefault,
                ParentVaultId = v.ParentVaultId,
                CreatedAt = v.CreatedAt,
                UpdatedAt = v.UpdatedAt,
                PersonIds = v.VaultPersons.Select(vp => vp.PersonId).ToList()
            };
            MergePlatformData(v.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableTopics = topics.Select(t =>
        {
            var dto = new PortableTopic
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            };
            MergePlatformData(t.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableTags = tags.Select(t =>
        {
            var dto = new PortableTag
            {
                Id = t.Id,
                Name = t.Name,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            };
            MergePlatformData(t.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portablePersons = persons.Select(p =>
        {
            var dto = new PortablePerson
            {
                Id = p.Id,
                Name = p.Name,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            };
            MergePlatformData(p.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableLocations = locations.Select(l =>
        {
            var dto = new PortableLocation
            {
                Id = l.Id,
                Name = l.Name,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt
            };
            MergePlatformData(l.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableEvents = events.Select(e =>
        {
            var dto = new PortableEvent
            {
                Id = e.Id,
                Name = e.Name,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt
            };
            MergePlatformData(e.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableInboxItems = inboxItems.Select(i =>
        {
            var dto = new PortableInboxItem
            {
                Id = i.Id,
                Body = i.Body,
                Type = i.Type,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt
            };
            MergePlatformData(i.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var availableLocationIds = locations.Select(l => l.Id).ToHashSet();
        var availableEventIds = events.Select(e => e.Id).ToHashSet();
        var portableKnowledge = knowledge.Select(k =>
        {
            var primaryVaultJunction = k.KnowledgeVaults.FirstOrDefault(kv => kv.IsPrimary);
            var dto = new PortableKnowledge
            {
                Id = k.Id,
                Title = k.Title,
                Content = k.Content,
                Summary = k.Summary,
                BriefSummary = k.BriefSummary,
                EncryptedFieldsJson = k.EncryptedFieldsJson,
                OriginalItemId = k.OriginalItemId,
                Type = k.Type,
                Source = k.Source,
                FilePath = k.FilePath,
                IsIndexed = k.IsIndexed,
                IndexedAt = k.IndexedAt,
                CreatedAt = k.CreatedAt,
                UpdatedAt = k.UpdatedAt,
                TopicId = k.TopicId,
                VaultIds = k.KnowledgeVaults.Select(kv => kv.VaultId).ToList(),
                PrimaryVaultId = primaryVaultJunction?.VaultId,
                TagIds = k.Tags.Select(t => t.Id).ToList(),
                PersonIds = k.KnowledgePersons.Select(kp => kp.PersonId).ToList(),
                LocationIds = k.KnowledgeLocations.Select(kl => kl.LocationId).ToList(),
                EventIds = k.KnowledgeEvents.Select(ke => ke.EventId).ToList(),
                // v2: rich links
                PersonLinks = k.KnowledgePersons.Select(kp => new PortableEntityLink
                {
                    EntityId = kp.PersonId,
                    RelationshipContext = kp.RelationshipContext,
                    Role = kp.Role,
                    Mentions = kp.Mentions,
                    ConfidenceScore = kp.ConfidenceScore
                }).ToList()
            };
            MergePlatformData(k.PlatformData, d => dto.ExtensionData = d);
            PortableKnowledgeLinks.Project(k, dto, availableLocationIds, availableEventIds);
            ProtectedKnowledgeFields.RedactCompanions(dto);
            return dto;
        }).ToList();

        var portableComments = comments.Select(c =>
        {
            var dto = new PortableKnowledgeComment
            {
                Id = c.Id,
                KnowledgeId = c.KnowledgeId,
                ParentCommentId = c.ParentCommentId,
                AuthorName = c.AuthorName,
                Body = c.Body,
                IsAnswer = c.IsAnswer,
                Sentiment = c.Sentiment,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            };
            MergePlatformData(c.PlatformData, d => dto.ExtensionData = d);
            return dto;
        }).ToList();

        var portableFiles = new List<PortableFileRecord>();
        var maxBinarySize = _options.MaxBinaryFileSizeMB * 1024L * 1024L;

        foreach (var f in fileRecords)
        {
            var dto = new PortableFileRecord
            {
                Id = f.Id,
                FileName = ProtectedFileMetadata.FileName(f),
                OriginalFileId = f.OriginalFileId ?? f.Id, EncryptedFieldsJson = f.EncryptedFieldsJson,
                FileVersion = f.FileVersion, RootFileRecordId = f.RootFileRecordId, PreviousFileRecordId = f.PreviousFileRecordId, IsCurrentVersion = f.IsCurrentVersion,
                ContentType = f.ContentType,
                SizeBytes = f.SizeBytes,
                BlobUri = f.BlobUri,
                TranscriptionText = f.TranscriptionText,
                ExtractedText = f.ExtractedText,
                VisionDescription = f.VisionDescription,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                Attachments = f.Attachments.Select(a => new PortableFileAttachmentLink
                {
                    KnowledgeId = a.KnowledgeId,
                    CommentId = a.CommentId
                }).ToList()
            };

            if (_options.IncludeBinaryContent &&
                !f.BlobMigrationPending &&
                f.SizeBytes <= maxBinarySize)
            {
                try
                {
                    var (stream, _, _) = await _storageProvider.DownloadAsync(f.TenantId, await SelfHostedFileObjects.ResolveStorageIdAsync(_db, f.TenantId, f.Id, ct), ct);
                    using (stream)
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream, ct);
                        dto.BinaryContentBase64 = Convert.ToBase64String(memoryStream.ToArray());
                    }

                    _logger.LogInformation(
                        "Exported binary content for file {FileRecordId} ({FileName}, {SizeBytes} bytes)",
                        f.Id, f.FileName, f.SizeBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to download binary for file {FileRecordId} ({FileName}). Excluding from export.",
                        f.Id, f.FileName);
                }
            }

            MergePlatformData(f.PlatformData, d => dto.ExtensionData = d);
            if (dto.ExtensionData?.Remove(PortableImportService.AttachmentMetadataKey, out var attachmentMetadata) == true)
            {
                var stored = attachmentMetadata.Deserialize<List<PortableFileAttachmentLink>>() ?? new();
                dto.Attachments = dto.Attachments.Select(link => stored.FirstOrDefault(a =>
                    a.KnowledgeId == link.KnowledgeId && a.CommentId == link.CommentId) ?? link).ToList();
                if (dto.ExtensionData.Count == 0) dto.ExtensionData = null;
            }

            portableFiles.Add(dto);
        }

        // Re-export archives: group by entity type, deserialize JSON back to JsonElement
        var archiveData = new Dictionary<string, List<JsonElement>>();
        foreach (var group in archives.GroupBy(a => a.EntityType))
        {
            archiveData[group.Key] = group
                .Select(a => JsonSerializer.Deserialize<JsonElement>(a.JsonData))
                .ToList();
        }

        var package = new PortableExportPackage
        {
            Scope = new ExportScope { Type = scope.Type, VaultIds = scope.VaultIds?.Distinct().ToList(), KnowledgeItemIds = scope.KnowledgeItemIds?.Distinct().ToList() },
            SchemaVersion = CoreSchema.Version,
            SourceEdition = "selfhosted",
            SourceTenantId = _tenantProvider.TenantId,
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata
            {
                TotalVaults = portableVaults.Count,
                TotalKnowledgeItems = portableKnowledge.Count,
                TotalTopics = portableTopics.Count,
                TotalTags = portableTags.Count,
                TotalPersons = portablePersons.Count,
                TotalLocations = portableLocations.Count,
                TotalEvents = portableEvents.Count,
                TotalInboxItems = portableInboxItems.Count,
                TotalComments = portableComments.Count,
                TotalFileRecords = portableFiles.Count,
                TotalArchiveTypes = archiveData.Count
            },
            Data = new PortableExportData
            {
                Vaults = portableVaults,
                KnowledgeItems = portableKnowledge,
                Topics = portableTopics,
                Tags = portableTags,
                Persons = portablePersons,
                Locations = portableLocations,
                Events = portableEvents,
                InboxItems = portableInboxItems,
                Comments = portableComments,
                FileRecords = portableFiles,
                Archives = archiveData
            }
        };

        _logger.LogInformation(
            "Portable export complete: {Vaults} vaults, {Knowledge} knowledge, {Topics} topics, {Tags} tags, {Persons} persons, {Locations} locations, {Events} events, {Inbox} inbox, {Comments} comments, {Files} files, {Archives} archive types",
            portableVaults.Count, portableKnowledge.Count, portableTopics.Count, portableTags.Count,
            portablePersons.Count, portableLocations.Count, portableEvents.Count, portableInboxItems.Count,
            portableComments.Count, portableFiles.Count, archiveData.Count);

        return package;
    }

    public Task<Stream> ExportZipAsync(CancellationToken ct = default)
        => ExportZipAsync(new ExportScope { Type = ExportScopeType.Tenant }, ct);

    public async Task<Stream> ExportZipAsync(ExportScope scope, CancellationToken ct = default)
    {
        var package = await ExportAsync(scope, ct);
        package.Mode = ExportMode.Full;

        var skippedItems = new List<PortableSkippedItem>();
        var tenantId = _tenantProvider.TenantId;

        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in package.Data.FileRecords)
            {
                file.BinaryContentBase64 = null;

                try
                {
                    var (downloadStream, _, _) = await _storageProvider.DownloadAsync(tenantId, await SelfHostedFileObjects.ResolveStorageIdAsync(_db, tenantId, file.Id, ct), ct);
                    using (downloadStream)
                    {
                        var ext = Path.GetExtension(file.FileName);
                        if (string.IsNullOrEmpty(ext)) ext = ".bin";
                        var entryPath = $"files/{file.Id}{ext}";

                        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await downloadStream.CopyToAsync(entryStream, ct);

                        file.BinaryFilePath = entryPath;
                        _logger.LogInformation(
                            "Added binary file to ZIP: {FileRecordId} ({FileName})",
                            file.Id, file.FileName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to download binary for file {FileRecordId} ({FileName}). Skipping in ZIP.",
                        file.Id, file.FileName);
                    skippedItems.Add(new PortableSkippedItem
                    {
                        Id = file.Id,
                        Title = file.FileName,
                        Reason = $"Binary download failed: {ex.Message}"
                    });
                }
            }

            PortableZipWriter.WriteToZip(archive, package, skippedItems);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static void ValidateScope(ExportScope scope)
    {
        var vaults = scope.VaultIds ?? [];
        var items = scope.KnowledgeItemIds ?? [];
        if (vaults.Contains(Guid.Empty) || items.Contains(Guid.Empty))
            throw new ArgumentException("Scope IDs must be non-empty UUIDs.");
        var valid = scope.Type switch
        {
            ExportScopeType.Tenant => vaults.Count == 0 && items.Count == 0,
            ExportScopeType.Vault or ExportScopeType.VaultWithChildren => vaults.Count > 0 && items.Count == 0,
            ExportScopeType.MultiSelect => items.Count > 0 && vaults.Count == 0,
            ExportScopeType.SingleItem => items.Count == 1 && vaults.Count == 0,
            _ => false
        };
        if (!valid) throw new ArgumentException("Scope requires matching vaultIds or knowledgeItemIds; SingleItem requires exactly one item and Tenant accepts no selectors.");
    }

    private static void MergePlatformData(string? platformData, Action<Dictionary<string, JsonElement>?> setter)
    {
        if (string.IsNullOrEmpty(platformData))
            return;

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(platformData);
        if (dict is { Count: > 0 })
            setter(dict);
    }
}
