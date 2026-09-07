namespace Knowz.Core.Portability;

using System.IO.Compression;
using System.Text.Json;
using Knowz.Core.Schema;

public static class PortableZipReader
{
    private const long MaxDecompressedSize = 5L * 1024 * 1024 * 1024; // 5 GB
    private const int MaxEntryCount = 500_000;
    private const double MaxDecompressionRatio = 10.0;

    public static PortableExportPackage ReadFromZip(
        ZipArchive archive,
        JsonSerializerOptions? options = null)
    {
        options ??= new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        ValidateSecurity(archive);

        if (IsLegacyFormat(archive))
        {
            return ReadLegacyFormat(archive, options);
        }

        return ReadPerItemFormat(archive, options);
    }

    private static void ValidateSecurity(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new PortableZipSecurityException(
                $"Archive contains {archive.Entries.Count} entries, exceeding the maximum of {MaxEntryCount}.");
        }

        long totalDecompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains("..") ||
                entry.FullName.StartsWith('/') ||
                entry.FullName.StartsWith('\\') ||
                entry.FullName.Contains('\\'))
            {
                throw new PortableZipSecurityException(
                    $"Archive entry '{entry.FullName}' contains a path traversal or invalid path character.");
            }

            totalDecompressed += entry.Length;
            totalCompressed += entry.CompressedLength;
        }

        if (totalDecompressed > MaxDecompressedSize)
        {
            throw new PortableZipSecurityException(
                $"Archive decompressed size ({totalDecompressed} bytes) exceeds the maximum of {MaxDecompressedSize} bytes.");
        }

        if (totalCompressed > 0 && (double)totalDecompressed / totalCompressed > MaxDecompressionRatio)
        {
            throw new PortableZipSecurityException(
                $"Archive decompression ratio ({(double)totalDecompressed / totalCompressed:F1}:1) exceeds the maximum of {MaxDecompressionRatio}:1.");
        }
    }

    private static bool IsLegacyFormat(ZipArchive archive)
    {
        var legacyManifest = archive.GetEntry("manifest.json");
        if (legacyManifest == null) return false;

        var newManifest = archive.GetEntry("_manifest.json");
        return newManifest == null;
    }

    private static PortableExportPackage ReadLegacyFormat(ZipArchive archive, JsonSerializerOptions options)
    {
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Legacy format detected but manifest.json not found.");

        using var stream = entry.Open();
        var package = JsonSerializer.Deserialize<PortableExportPackage>(stream, options)
            ?? throw new InvalidOperationException("Failed to deserialize legacy manifest.json.");

        return package;
    }

    private static PortableExportPackage ReadPerItemFormat(ZipArchive archive, JsonSerializerOptions options)
    {
        var vaults = ReadJsonEntry<List<PortableVault>>(archive, "_vaults.json", options) ?? new();
        var persons = ReadJsonEntry<List<PortablePerson>>(archive, "_persons.json", options) ?? new();
        var tags = ReadJsonEntry<List<PortableTag>>(archive, "_tags.json", options) ?? new();
        var topics = ReadJsonEntry<List<PortableTopic>>(archive, "_topics.json", options) ?? new();
        var locations = ReadJsonEntry<List<PortableLocation>>(archive, "_locations.json", options) ?? new();
        var events = ReadJsonEntry<List<PortableEvent>>(archive, "_events.json", options) ?? new();
        var inboxItems = ReadJsonEntry<List<PortableInboxItem>>(archive, "_inbox.json", options) ?? new();

        var knowledgeItems = new List<PortableKnowledge>();
        var allComments = new List<PortableKnowledgeComment>();
        var allFileRecords = new List<PortableFileRecord>();

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("items/")) continue;
            var fileName = entry.FullName["items/".Length..];

            if (fileName.EndsWith("-comments.json"))
            {
                var comments = ReadJsonEntryDirect<List<PortableKnowledgeComment>>(entry, options);
                if (comments != null) allComments.AddRange(comments);
            }
            else if (fileName.EndsWith("-attachments.json"))
            {
                var files = ReadJsonEntryDirect<List<PortableFileRecord>>(entry, options);
                if (files != null) allFileRecords.AddRange(files);
            }
            else if (fileName.EndsWith(".json"))
            {
                var knowledge = ReadJsonEntryDirect<PortableKnowledge>(entry, options);
                if (knowledge != null) knowledgeItems.Add(knowledge);
            }
        }

        var manifestEntry = archive.GetEntry("_manifest.json");
        JsonElement? manifest = manifestEntry != null
            ? ReadJsonEntryDirect<JsonElement>(manifestEntry, options)
            : null;

        ExportScope? scope = null;
        var sourceEdition = "platform";
        var sourceTenantId = Guid.Empty;
        var exportedAt = DateTime.MinValue;
        var schemaVersion = CoreSchema.Version;
        var mode = ExportMode.Full;

        if (manifest is JsonElement m2)
        {
            if (m2.TryGetProperty("scope", out var scopeEl))
                scope = JsonSerializer.Deserialize<ExportScope>(scopeEl.GetRawText(), options);
            if (m2.TryGetProperty("sourceEdition", out var edEl))
                sourceEdition = edEl.GetString() ?? "platform";
            if (m2.TryGetProperty("sourceTenantId", out var tidEl) && tidEl.TryGetGuid(out var tid))
                sourceTenantId = tid;
            if (m2.TryGetProperty("exportedAt", out var expEl) && expEl.TryGetDateTime(out var expDt))
                exportedAt = expDt;
            if (m2.TryGetProperty("schemaVersion", out var svEl) && svEl.TryGetInt32(out var sv))
                schemaVersion = sv;
            if (m2.TryGetProperty("mode", out var modeEl) && Enum.TryParse<ExportMode>(modeEl.GetString(), true, out var mp))
                mode = mp;
        }

        return new PortableExportPackage
        {
            SchemaVersion = schemaVersion,
            SourceEdition = sourceEdition,
            SourceTenantId = sourceTenantId,
            ExportedAt = exportedAt,
            Mode = mode,
            Scope = scope,
            Metadata = new PortableExportMetadata
            {
                TotalVaults = vaults.Count,
                TotalKnowledgeItems = knowledgeItems.Count,
                TotalTopics = topics.Count,
                TotalTags = tags.Count,
                TotalPersons = persons.Count,
                TotalLocations = locations.Count,
                TotalEvents = events.Count,
                TotalInboxItems = inboxItems.Count,
                TotalComments = allComments.Count,
                TotalFileRecords = allFileRecords.Count,
            },
            Data = new PortableExportData
            {
                Vaults = vaults,
                KnowledgeItems = knowledgeItems,
                Topics = topics,
                Tags = tags,
                Persons = persons,
                Locations = locations,
                Events = events,
                InboxItems = inboxItems,
                Comments = allComments,
                FileRecords = allFileRecords,
            }
        };
    }

    private static T? ReadJsonEntry<T>(ZipArchive archive, string entryName, JsonSerializerOptions options)
    {
        var entry = archive.GetEntry(entryName);
        if (entry == null) return default;
        return ReadJsonEntryDirect<T>(entry, options);
    }

    private static T? ReadJsonEntryDirect<T>(ZipArchiveEntry entry, JsonSerializerOptions options)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, options);
    }
}

public class PortableZipSecurityException : Exception
{
    public PortableZipSecurityException(string message) : base(message) { }
}
