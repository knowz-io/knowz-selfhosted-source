using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;

namespace Knowz.SelfHosted.Application.Services;

internal static class ProtectedFileMetadata
{
    public static string FileName(FileRecord file)
    {
        if (file.EncryptedFieldsJson == null) return file.FileName;
        using var document = StrictJson.Parse(file.EncryptedFieldsJson, 1024 * 1024);
        if (document.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("fileName", out _)) return "encrypted-file";
        return file.FileName;
    }
}
