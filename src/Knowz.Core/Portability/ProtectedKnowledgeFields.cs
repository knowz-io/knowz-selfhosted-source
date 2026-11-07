using System.Text.Json;
namespace Knowz.Core.Portability;
/// <summary>Public companion projection only. The exact encrypted sidecar is never rewritten.</summary>
public static class ProtectedKnowledgeFields
{
    public static bool HasPlaintextExtensionCompanion(PortableKnowledge item)
    {
        if (string.IsNullOrWhiteSpace(item.EncryptedFieldsJson) || item.ExtensionData is null) return false;
        using var document = StrictJson.Parse(item.EncryptedFieldsJson, 8 * 1024 * 1024);
        var protectedNames = document.RootElement.GetProperty("fields").EnumerateObject()
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return item.ExtensionData.Any(entry => protectedNames.Contains(entry.Key)
            && entry.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && (entry.Value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(entry.Value.GetString())));
    }

    public static void RedactCompanions(PortableKnowledge item)
    {
        if (string.IsNullOrWhiteSpace(item.EncryptedFieldsJson)) return;
        using var document = StrictJson.Parse(item.EncryptedFieldsJson, 8 * 1024 * 1024);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) || schema.GetString() != "knowz-encrypted-fields-v1"
            || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw new KnowzArchiveException("Current protected fields use an unsupported profile.");
        if (fields.TryGetProperty("title", out _)) item.Title = "Encrypted item";
        if (fields.TryGetProperty("content", out _)) item.Content = "";
        if (fields.TryGetProperty("summary", out _)) item.Summary = null;
        if (fields.TryGetProperty("briefSummary", out _)) item.BriefSummary = null;
        foreach (var field in fields.EnumerateObject())
            foreach (var name in item.ExtensionData?.Keys.Where(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? [])
                item.ExtensionData!.Remove(name);
    }
}
