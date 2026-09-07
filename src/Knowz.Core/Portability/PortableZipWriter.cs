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
        JsonSerializerOptions? options = null)
    {
        options ??= new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        WriteManifest(archive, package, skippedItems, options);
        WriteJsonEntry(archive, "_vaults.json", package.Data.Vaults, options);
        WriteJsonEntry(archive, "_persons.json", package.Data.Persons, options);
        WriteJsonEntry(archive, "_tags.json", package.Data.Tags, options);
        WriteJsonEntry(archive, "_topics.json", package.Data.Topics, options);
        WriteJsonEntry(archive, "_locations.json", package.Data.Locations, options);
        WriteJsonEntry(archive, "_events.json", package.Data.Events, options);
        WriteJsonEntry(archive, "_inbox.json", package.Data.InboxItems, options);
        WriteJsonEntry(archive, "_skipped.json", skippedItems ?? new List<PortableSkippedItem>(), options);

        WritePerItemFiles(archive, package, options);
    }

    private static void WriteManifest(
        ZipArchive archive,
        PortableExportPackage package,
        List<PortableSkippedItem>? skippedItems,
        JsonSerializerOptions options)
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

        var filesByKnowledge = package.Data.FileRecords
            .Where(f => f.Attachments.Any(a => a.KnowledgeId.HasValue))
            .GroupBy(f => f.Attachments.First(a => a.KnowledgeId.HasValue).KnowledgeId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

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
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string entryName, T data, JsonSerializerOptions options)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var json = JsonSerializer.SerializeToUtf8Bytes(data, options);
        stream.Write(json);
    }
}

public class PortableSkippedItem
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string Reason { get; set; } = string.Empty;
}
