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

        try
        {
            var package = IsLegacyFormat(archive)
                ? ReadLegacyFormat(archive, options)
                : ReadPerItemFormat(archive, options);
            ValidatePackage(package);
            return package;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new PortableZipSecurityException($"Invalid portable ZIP package: {ex.Message}");
        }
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

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entryNames.Add(entry.FullName))
                throw new PortableZipSecurityException($"Duplicate ZIP entry: {entry.FullName}.");
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

        // Small highly compressible exports are normal. Apply the ratio guard only after
        // a bounded 100 MiB expansion, while retaining the absolute cap for every archive.
        if (totalDecompressed > 100L * 1024 * 1024 && totalCompressed > 0 && (double)totalDecompressed / totalCompressed > MaxDecompressionRatio)
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

        var manifest = ReadJsonEntryDirect<JsonElement>(entry, options);
        RequireManifest(manifest, legacy: true);
        var package = manifest.Deserialize<PortableExportPackage>(options)
            ?? throw new PortableZipSecurityException("Invalid legacy manifest.json.");

        return package;
    }

    private static PortableExportPackage ReadPerItemFormat(ZipArchive archive, JsonSerializerOptions options)
    {
        var manifestEntry = archive.GetEntry("_manifest.json")
            ?? throw new PortableZipSecurityException("Portable ZIP is missing _manifest.json or manifest.json.");
        var manifest = ReadJsonEntryDirect<JsonElement>(manifestEntry, options);
        RequireManifest(manifest, legacy: false);

        var vaults = ReadJsonEntry<List<PortableVault>>(archive, "_vaults.json", options) ?? new();
        var persons = ReadJsonEntry<List<PortablePerson>>(archive, "_persons.json", options) ?? new();
        var tags = ReadJsonEntry<List<PortableTag>>(archive, "_tags.json", options) ?? new();
        var topics = ReadJsonEntry<List<PortableTopic>>(archive, "_topics.json", options) ?? new();
        var locations = ReadJsonEntry<List<PortableLocation>>(archive, "_locations.json", options) ?? new();
        var events = ReadJsonEntry<List<PortableEvent>>(archive, "_events.json", options) ?? new();
        var inboxItems = ReadJsonEntry<List<PortableInboxItem>>(archive, "_inbox.json", options) ?? new();

        var knowledgeItems = new List<PortableKnowledge>();
        var allComments = ReadJsonEntry<List<PortableKnowledgeComment>>(archive, "_orphan-comments.json", options) ?? new();
        var archives = ReadJsonEntry<Dictionary<string, List<JsonElement>>>(archive, "_archives.json", options) ?? new();
        var allFileRecords = new List<PortableFileRecord>();

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == "_vault-files.json")
            {
                var vaultScoped = ReadJsonEntryDirect<List<PortableFileRecord>>(entry, options);
                if (vaultScoped != null) allFileRecords.AddRange(vaultScoped);
                continue;
            }
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
                TotalArchiveTypes = archives.Count,
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
                Archives = archives,
            }
        };
    }

    private static void RequireManifest(JsonElement manifest, bool legacy)
    {
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version < 1)
            throw new PortableZipSecurityException("Portable ZIP manifest requires a positive schemaVersion.");
        if (legacy)
        {
            if (!manifest.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new PortableZipSecurityException("Legacy portable ZIP manifest requires a data object.");
        }
        else if (!manifest.TryGetProperty("formatVersion", out var format) ||
            format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var formatVersion) || formatVersion != 1)
            throw new PortableZipSecurityException("Unsupported or missing portable ZIP formatVersion; expected 1.");
    }

    /// <summary>Validate collection structure before callers perform any import side effects.</summary>
    public static void ValidatePackage(PortableExportPackage package)
    {
        if (package?.Data == null || package.Metadata == null)
            throw new PortableZipSecurityException("Portable package requires data and metadata objects.");
        var data = package.Data;
        ValidateEntities(data.Vaults, v => v.Id, "vaults");
        ValidateEntities(data.KnowledgeItems, v => v.Id, "knowledge items");
        ValidateEntities(data.Topics, v => v.Id, "topics");
        ValidateEntities(data.Tags, v => v.Id, "tags");
        ValidateEntities(data.Persons, v => v.Id, "persons");
        ValidateEntities(data.Locations, v => v.Id, "locations");
        ValidateEntities(data.Events, v => v.Id, "events");
        ValidateEntities(data.InboxItems, v => v.Id, "inbox items");
        ValidateEntities(data.Comments, v => v.Id, "comments");
        ValidateEntities(data.FileRecords, v => v.Id, "files");
        if (data.Archives == null || data.Archives.Values.Any(entries => entries == null || entries.Any(e => e.ValueKind != JsonValueKind.Object)))
            throw new PortableZipSecurityException("Portable archives must contain arrays of entity objects.");
        if (data.FileRecords.Any(f => f.Attachments == null || f.Attachments.Any(a => a == null)))
            throw new PortableZipSecurityException("Portable file attachments must be arrays of objects.");
        if (data.Vaults.Any(v => v.PersonIds == null) || data.KnowledgeItems.Any(k =>
            k.VaultIds == null || k.TagIds == null || k.PersonIds == null || k.LocationIds == null || k.EventIds == null))
            throw new PortableZipSecurityException("Portable relationship ID collections must be arrays.");
        if (data.KnowledgeItems.Any(k => k.PersonLinks?.Any(l => l == null) == true ||
            k.LocationLinks?.Any(l => l == null) == true || k.EventLinks?.Any(l => l == null) == true ||
            k.Relationships?.Any(l => l == null) == true))
            throw new PortableZipSecurityException("Portable relationship links must not contain null entries.");

    }

    private static void ValidateEntities<T>(IEnumerable<T>? entries, Func<T, Guid> id, string label)
    {
        if (entries == null) throw new PortableZipSecurityException($"Portable {label} must be an array.");
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
            if (entry == null || !ids.Add(id(entry)))
                throw new PortableZipSecurityException($"Portable {label} contains a null entity or duplicate ID.");
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
        return JsonSerializer.Deserialize<T>(stream, options)
            ?? throw new PortableZipSecurityException($"ZIP entry '{entry.FullName}' must not be null.");
    }
}

public class PortableZipSecurityException : Exception
{
    public PortableZipSecurityException(string message) : base(message) { }
}
