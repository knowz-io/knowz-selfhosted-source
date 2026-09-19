namespace Knowz.Core.Portability;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Knowz.Core.Schema;

public static class PortableZipWriter
{
    public static void WriteToZip(
        ZipArchive archive,
        PortableExportPackage package,
        List<PortableSkippedItem>? skippedItems = null,
        JsonSerializerOptions? options = null,
        PortableZipPartManifestInfo? partInfo = null)
    {
        options ??= new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        partInfo ??= new PortableZipPartManifestInfo(1, 1, Guid.NewGuid().ToString());
        WriteManifest(archive, package, skippedItems, options, partInfo);
        WriteJsonEntry(archive, "_vaults.json", package.Data.Vaults, options);
        WriteJsonEntry(archive, "_persons.json", package.Data.Persons, options);
        WriteJsonEntry(archive, "_tags.json", package.Data.Tags, options);
        WriteJsonEntry(archive, "_topics.json", package.Data.Topics, options);
        WriteJsonEntry(archive, "_locations.json", package.Data.Locations, options);
        WriteJsonEntry(archive, "_events.json", package.Data.Events, options);
        WriteJsonEntry(archive, "_inbox.json", package.Data.InboxItems, options);
        WriteJsonEntry(archive, "_skipped.json", skippedItems ?? new List<PortableSkippedItem>(), options);

        if (package.Data.Archives.Count > 0)
            WriteJsonEntry(archive, "_archives.json", package.Data.Archives, options);
        WritePerItemFiles(archive, package, options);
    }

    private static void WriteManifest(
        ZipArchive archive,
        PortableExportPackage package,
        List<PortableSkippedItem>? skippedItems,
        JsonSerializerOptions options,
        PortableZipPartManifestInfo partInfo)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = package.SchemaVersion,
            ["formatVersion"] = 1,
            ["sourceEdition"] = package.SourceEdition,
            ["sourceTenantId"] = package.SourceTenantId,
            ["exportedAt"] = package.ExportedAt,
            ["mode"] = package.Mode.ToString(),
            ["scope"] = package.Scope,
            ["counts"] = new Dictionary<string, int>
            {
                ["vaults"] = package.Metadata.TotalVaults,
                ["knowledgeItems"] = package.Metadata.TotalKnowledgeItems,
                ["topics"] = package.Metadata.TotalTopics,
                ["tags"] = package.Metadata.TotalTags,
                ["persons"] = package.Metadata.TotalPersons,
                ["locations"] = package.Metadata.TotalLocations,
                ["events"] = package.Metadata.TotalEvents,
                ["inboxItems"] = package.Metadata.TotalInboxItems,
                ["comments"] = package.Metadata.TotalComments,
                ["fileRecords"] = package.Metadata.TotalFileRecords,
            },
            ["skippedCount"] = skippedItems?.Count ?? 0,
            ["part"] = partInfo.Part,
            ["totalParts"] = partInfo.TotalParts,
            ["exportId"] = partInfo.ExportId,
        };

        WriteJsonEntry(archive, "_manifest.json", manifest, options);
    }

    private static void WritePerItemFiles(
        ZipArchive archive,
        PortableExportPackage package,
        JsonSerializerOptions options)
    {
        var commentsByKnowledge = package.Data.Comments
            .GroupBy(c => c.KnowledgeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var knowledgeIds = package.Data.KnowledgeItems.Select(k => k.Id).ToHashSet();
        var orphanComments = package.Data.Comments.Where(c => !knowledgeIds.Contains(c.KnowledgeId)).ToList();
        if (orphanComments.Count > 0)
            WriteJsonEntry(archive, "_orphan-comments.json", orphanComments, options);

        var commentOwners = package.Data.Comments
            .ToDictionary(comment => comment.Id, comment => comment.KnowledgeId);
        var filesByKnowledge = new Dictionary<Guid, List<PortableFileRecord>>();
        var vaultScopedFiles = new List<PortableFileRecord>();
        foreach (var file in package.Data.FileRecords)
        {
            var ownerId = file.Attachments
                .Where(attachment => attachment.KnowledgeId.HasValue && knowledgeIds.Contains(attachment.KnowledgeId.Value))
                .Select(attachment => attachment.KnowledgeId)
                .FirstOrDefault();
            if (!ownerId.HasValue)
            {
                foreach (var attachment in file.Attachments)
                {
                    if (attachment.CommentId.HasValue
                        && commentOwners.TryGetValue(attachment.CommentId.Value, out var knowledgeId)
                        && knowledgeIds.Contains(knowledgeId))
                    {
                        ownerId = knowledgeId;
                        break;
                    }
                }
            }

            if (!ownerId.HasValue)
            {
                // A file uploaded straight to a vault has no owning knowledge item. Skipping it
                // here silently dropped vault content from every archive (F8) — give those
                // records a dedicated entry instead of a per-item home.
                vaultScopedFiles.Add(file);
                continue;
            }
            if (!filesByKnowledge.TryGetValue(ownerId.Value, out var files))
            {
                files = new List<PortableFileRecord>();
                filesByKnowledge[ownerId.Value] = files;
            }
            files.Add(file);
        }

        foreach (var knowledge in package.Data.KnowledgeItems)
        {
            WriteJsonEntry(archive, $"items/{knowledge.Id}.json", knowledge, options);

            if (commentsByKnowledge.TryGetValue(knowledge.Id, out var comments) && comments.Count > 0)
            {
                WriteJsonEntry(archive, $"items/{knowledge.Id}-comments.json", comments, options);
            }

            if (filesByKnowledge.TryGetValue(knowledge.Id, out var files) && files.Count > 0)
            {
                WriteJsonEntry(archive, $"items/{knowledge.Id}-attachments.json", files, options);
            }
        }

        if (vaultScopedFiles.Count > 0)
        {
            // Additive entry: readers that predate it simply ignore unknown archive names.
            WriteJsonEntry(archive, "_vault-files.json", vaultScopedFiles, options);
        }
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string entryName, T data, JsonSerializerOptions options)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var json = JsonSerializer.SerializeToUtf8Bytes(data, options);
        stream.Write(json);
    }
}

/// <summary>Manifest identity shared by every archive in one multi-part export.</summary>
public sealed record PortableZipPartManifestInfo(int Part, int TotalParts, string ExportId);

public class PortableSkippedItem
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string Reason { get; set; } = string.Empty;
}
