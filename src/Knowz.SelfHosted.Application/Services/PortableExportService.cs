namespace Knowz.SelfHosted.Application.Services;

using System.IO.Compression;
using System.Text.Json;
using Knowz.Core.Configuration;
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

    public async Task<PortableExportPackage> ExportAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting portable export for tenant {TenantId}", _tenantProvider.TenantId);

        // Query all entities with includes for junctions (AsNoTracking for read-only)
        var knowledge = await _db.KnowledgeItems
            .Include(k => k.KnowledgeVaults)
            .Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations)
            .Include(k => k.KnowledgeEvents)
            .Include(k => k.Tags)
            .AsNoTracking()
            .ToListAsync(ct);

        var vaults = await _db.Vaults
            .Include(v => v.VaultPersons)
            .AsNoTracking()
            .ToListAsync(ct);
        var topics = await _db.Topics.AsNoTracking().ToListAsync(ct);
        var tags = await _db.Tags.AsNoTracking().ToListAsync(ct);
        var persons = await _db.Persons.AsNoTracking().ToListAsync(ct);
        var locations = await _db.Locations.AsNoTracking().ToListAsync(ct);
        var events = await _db.Events.AsNoTracking().ToListAsync(ct);
        var inboxItems = await _db.InboxItems.AsNoTracking().ToListAsync(ct);
        var comments = await _db.Comments
            .AsNoTracking()
            .ToListAsync(ct);
        var fileRecords = await _db.FileRecords
            .Include(f => f.Attachments)
            .AsNoTracking()
            .ToListAsync(ct);
        var archives = await _db.PortableArchives
            .Where(a => a.TenantId == _tenantProvider.TenantId)
            .AsNoTracking()
            .ToListAsync(ct);

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
                FileName = f.FileName,
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
                    var (stream, _, _) = await _storageProvider.DownloadAsync(f.TenantId, f.Id, ct);
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

    public async Task<Stream> ExportZipAsync(CancellationToken ct = default)
    {
        var package = await ExportAsync(ct);
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
                    var (downloadStream, _, _) = await _storageProvider.DownloadAsync(tenantId, file.Id, ct);
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

    private static void MergePlatformData(string? platformData, Action<Dictionary<string, JsonElement>?> setter)
    {
        if (string.IsNullOrEmpty(platformData))
            return;

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(platformData);
        if (dict is { Count: > 0 })
            setter(dict);
    }
}
