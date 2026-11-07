using System.Text.Json;

namespace Knowz.Core.Portability;

/// <summary>
/// Rejects request documents that declare private key material.
/// Ciphertext and public keys are allowed. Matched values are never returned.
/// </summary>
public static class SealedMaterialGuard
{
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "privatekey",
        "recoveryphrase",
        "seedphrase",
        "mnemonic",
        "wrappedprivatekey",
        "x25519privatekey",
        "secretkey",
        "keymaterial",
    };

    public static bool DeclaresPrivateMaterial(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return Walk(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (ForbiddenNames.Contains(NormalizeName(prop.Name)))
                        return true;
                    if (Walk(prop.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Walk(item))
                        return true;
                }
                return false;
            case JsonValueKind.String:
                var text = element.GetString();
                return LooksLikeJsonObject(text) && DeclaresPrivateMaterial(text);
            default:
                return false;
        }
    }

    private static string NormalizeName(string name)
    {
        Span<char> buffer = name.Length <= 64 ? stackalloc char[name.Length] : new char[name.Length];
        var written = 0;
        foreach (var ch in name)
        {
            if (ch is '_' or '-')
                continue;
            buffer[written++] = ch;
        }

        return new string(buffer[..written]);
    }

    private static bool LooksLikeJsonObject(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.AsSpan().TrimStart().StartsWith('{');
}
