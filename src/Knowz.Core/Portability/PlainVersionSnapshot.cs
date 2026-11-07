using System.Text.Json;

namespace Knowz.Core.Portability;

/// <summary>Ordinary history may contain encrypted fields, but never an opaque encrypted body.</summary>
public static class PlainVersionSnapshot
{
    public static string? ReadEncryptedFields(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        using var doc = StrictJson.Parse(metadata, KnowzArchiveLimits.MaxPackageBytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new JsonException("Plain history snapshot metadata must be an unambiguous object.");
        string? fields = null;
        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (name is "sealedsnapshot" or "isencrypted" or "pendingpostenrichmentencrypt")
            {
                if (property.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) throw new JsonException("Encrypted body history cannot be represented as ordinary history.");
            }
            if (name is "encrypteddek" or "encrypteddeknonce" or "contentnonce" or "wrappeddek" or "deknonce" or "ephemeralpublickey"
                or "cryptoversion" or "encryptionkeyref" or "portablerecoverystate")
            {
                if (property.Value.ValueKind != JsonValueKind.Null && !(property.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(property.Value.GetString())))
                    throw new JsonException("Encrypted body history cannot be represented as ordinary history.");
            }
            if (name != "encryptedfieldsjson") continue;
            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new JsonException("Encrypted history fields must be serialized JSON.");
            fields = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        if (!string.IsNullOrWhiteSpace(fields))
        {
            using var fieldDocument = StrictJson.Parse(fields, KnowzArchiveLimits.MaxPackageBytes);
            if (fieldDocument.RootElement.TryGetProperty("fields", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                var protectedNames = values.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (root.EnumerateObject().Any(p => protectedNames.Contains(p.Name)
                    && p.Value.ValueKind != JsonValueKind.Null
                    && !(p.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(p.Value.GetString()))))
                    throw new JsonException("Protected history fields have plaintext metadata companions.");
            }
        }
        return fields;
    }
}
