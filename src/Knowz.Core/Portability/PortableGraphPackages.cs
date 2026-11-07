using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowz.Core.Portability;

/// <summary>Package members partition one authenticated graph; relationship closure is evaluated after assembly.</summary>
public static class PortableGraphPackages
{
    private static readonly string[] TopCollections = ["cryptoContexts", "cryptoKeyCatalog", "sealedItems", "sealedVersions",
        "sealedFiles", "sealedComments", "omissions", "vaultCryptoContextLinks", "payloadKeyReferences", "sealedAttachments",
        "suspendedReleasePolicies", "dependencyBoundItems", "plainVersions"];
    private static readonly string[] DataCollections = ["vaults", "knowledgeItems", "topics", "tags", "persons", "locations",
        "events", "inboxItems", "comments", "fileRecords"];
    private static readonly HashSet<string> SharedCollections = ["cryptoContexts", "cryptoKeyCatalog", "vaultCryptoContextLinks",
        "vaults", "topics", "tags", "persons", "locations", "events"];

    public static PortableExportPackage Combine(IReadOnlyList<PortableExportPackage> packages)
    {
        if (packages.Count is < 1 or > 128 || packages.Any(p => p == null || p.Data == null))
            throw new KnowzArchiveException("Archive requires valid graph packages.");
        var result = AsNode(packages[0]);
        var expectedHeader = Header(result).ToJsonString();
        var identities = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in TopCollections.Concat(DataCollections)) identities[name] = new(StringComparer.OrdinalIgnoreCase);
        var combined = Header(result);
        foreach (var package in packages)
        {
            var node = AsNode(package);
            if (Header(node).ToJsonString() != expectedHeader)
                throw new KnowzArchiveException("Archive package headers disagree.");
            foreach (var name in TopCollections) Append(combined, node, name);
            var sourceData = node["data"] as JsonObject ?? throw new KnowzArchiveException("Package data is missing.");
            var destinationData = (JsonObject)combined["data"]!;
            foreach (var name in DataCollections) Append(destinationData, sourceData, name);
            if (sourceData["archives"] is not JsonObject archives) throw new KnowzArchiveException("Archive collections are missing.");
            foreach (var collection in archives)
            {
                if (collection.Value is not JsonArray rows) throw new KnowzArchiveException("Opaque archive collection is invalid.");
                var target = (JsonObject)destinationData["archives"]!;
                var values = target[collection.Key] as JsonArray ?? new JsonArray();
                target[collection.Key] ??= values;
                foreach (var row in rows) values.Add(row?.DeepClone() ?? throw new KnowzArchiveException("Null archive record."));
            }
        }
        var assembled = combined.Deserialize<PortableExportPackage>(ArchiveJson.Options)
            ?? throw new KnowzArchiveException("Combined graph is missing.");
        KnowzArchiveWriter.ValidatePackageSet(packages);
        return assembled;

        void Append(JsonObject target, JsonObject source, string name)
        {
            if (source[name] == null)
            {
                if (DataCollections.Contains(name)) throw new KnowzArchiveException("Required graph collection is missing.");
                return;
            }
            if (source[name] is not JsonArray rows) throw new KnowzArchiveException("Graph collection is invalid.");
            var values = (JsonArray)target[name]!;
            foreach (var row in rows)
            {
                if (row is not JsonObject record) throw new KnowzArchiveException("Graph contains a null or invalid record.");
                if (SharedCollections.Contains(name))
                {
                    var key = name switch
                    {
                        "cryptoContexts" => Text(record, "contextId"),
                        "cryptoKeyCatalog" => Text(record, "contextId") + "|" + record["generation"]?.ToJsonString(),
                        "vaultCryptoContextLinks" => Text(record, "vaultId") + "|" + Text(record, "contextId"),
                        _ => Text(record, "id"),
                    };
                    var serialized = Canonical(record);
                    if (identities[name].TryGetValue(key, out var prior))
                    {
                        if (prior != serialized) throw new KnowzArchiveException("Archive has conflicting shared metadata across packages.");
                        continue;
                    }
                    identities[name].Add(key, serialized);
                }
                values.Add(record.DeepClone());
            }
        }
    }

    public static IReadOnlyList<PortableExportPackage> Partition(PortableExportPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var source = JsonSerializer.SerializeToNode(package, ArchiveJson.MetadataOptions) as JsonObject
            ?? throw new KnowzArchiveException("Graph is missing.");
        var header = Header(source); var headerBytes = Encoding.UTF8.GetByteCount(header.ToJsonString());
        if (headerBytes > KnowzArchiveLimits.MaxPackageBytes - 256) throw new KnowzArchiveException("Graph header exceeds the package limit.");
        var current = (JsonObject)header.DeepClone(); var bytes = (long)headerBytes; var metadataBytes = 0L;
        var result = new List<PortableExportPackage>(); var rowCount = 0;
        foreach (var name in TopCollections) AddRows(source, name, data: false);
        var data = source["data"] as JsonObject ?? throw new KnowzArchiveException("Graph data is missing.");
        foreach (var name in DataCollections) AddRows(data, name, data: true);
        if (data["archives"] is not JsonObject archives) throw new KnowzArchiveException("Archive collections are missing.");
        foreach (var collection in archives)
        {
            if (collection.Value is not JsonArray rows) throw new KnowzArchiveException("Opaque archive collection is invalid.");
            foreach (var row in rows) Add(row, collection.Key, data: true, opaque: true);
        }
        Finish();
        // Inline source bytes are carried only in the producer's objects, never partition sizing metadata.
        var sealedFiles = (package.SealedFiles ?? []).ToDictionary(f => f.Id);
        var plainFiles = package.Data.FileRecords.ToDictionary(f => f.Id);
        foreach (var part in result)
        {
            foreach (var file in part.SealedFiles ?? []) file.BinaryContentBase64 = sealedFiles[file.Id].BinaryContentBase64;
            foreach (var file in part.Data.FileRecords) file.BinaryContentBase64 = plainFiles[file.Id].BinaryContentBase64;
        }
        return result;

        void AddRows(JsonObject container, string name, bool data)
        {
            if (container[name] == null) return;
            if (container[name] is not JsonArray rows) throw new KnowzArchiveException("Graph collection is invalid.");
            foreach (var row in rows) Add(row, name, data, opaque: false);
        }
        void Add(JsonNode? row, string name, bool data, bool opaque)
        {
            if (row == null) throw new KnowzArchiveException("Graph contains a null record.");
            var rowBytes = Encoding.UTF8.GetByteCount(row.ToJsonString()) + 1L;
            // Generated binary path/hash and a missing original identity expand file metadata.
            if (name is "sealedFiles" or "fileRecords") rowBytes += 256;
            if (opaque) rowBytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(name)) + 4;
            if (headerBytes + rowBytes > KnowzArchiveLimits.MaxPackageBytes - 256)
                throw new KnowzArchiveException("A graph record exceeds the supported package limit.");
            if (bytes + rowBytes > KnowzArchiveLimits.MaxPackageBytes - 256) Finish();
            var container = data ? (JsonObject)current["data"]! : current;
            if (opaque)
            {
                var target = (JsonObject)container["archives"]!;
                var values = target[name] as JsonArray ?? new JsonArray(); target[name] ??= values; values.Add(row.DeepClone());
            }
            else ((JsonArray)container[name]!).Add(row.DeepClone());
            bytes += rowBytes; rowCount++;
        }
        void Finish()
        {
            if (rowCount == 0 && result.Count > 0) return;
            if (result.Count >= 128 || (metadataBytes += bytes) > KnowzArchiveLimits.MaxMetadataBytes)
                throw new KnowzArchiveException("Archive metadata exceeds its aggregate limit.");
            result.Add(current.Deserialize<PortableExportPackage>(ArchiveJson.Options)!);
            current = (JsonObject)header.DeepClone(); bytes = headerBytes; rowCount = 0;
        }
    }

    private static JsonObject AsNode(PortableExportPackage package) => JsonSerializer.SerializeToNode(package, ArchiveJson.Options) as JsonObject
        ?? throw new KnowzArchiveException("Graph is missing.");
    private static JsonObject Header(JsonObject source)
    {
        var header = (JsonObject)source.DeepClone();
        foreach (var name in TopCollections) header[name] = new JsonArray();
        var data = header["data"] as JsonObject ?? throw new KnowzArchiveException("Graph data is missing.");
        foreach (var name in DataCollections) data[name] = new JsonArray();
        data["archives"] = new JsonObject();
        return header;
    }
    private static string Canonical(JsonNode node)
    {
        if (node is JsonObject obj)
            return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                JsonSerializer.Serialize(p.Key) + ":" + (p.Value == null ? "null" : Canonical(p.Value)))) + "}";
        if (node is JsonArray array)
            return "[" + string.Join(",", array.Select(value => value == null ? "null" : Canonical(value))) + "]";
        return node.ToJsonString();
    }

    private static string Text(JsonObject node, string name) => node[name]?.GetValue<string>()?.ToLowerInvariant()
        ?? throw new KnowzArchiveException("Shared graph identity is missing.");
}
