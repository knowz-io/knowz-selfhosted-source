using System.Text;
using System.Text.Json;
using Knowz.Core.Schema;

namespace Knowz.Core.Portability;

/// <summary>
/// Frozen <c>.knowzarchive</c> profile. Distinct from plaintext
/// <see cref="CoreSchema.Version"/> (2) and sealed sync schema 3.
/// Multipart parts are named here and are not produced or accepted yet.
/// </summary>
public static class KnowzArchiveProfile
{
    public const string FileExtension = ".knowzarchive";
    public const string MediaType = "application/vnd.knowz.archive+json";
    public const string FormatId = "knowz.archive";
    public const int FormatVersion = 1;
    public const int SchemaVersion = 4;
    public const string Capability = "knowz.archive/v1";
    public const string MultipartNotProduced = "multipart-not-produced";

    public const string PropFormatId = "formatId";
    public const string PropFormatVersion = "formatVersion";
    public const string PropSchemaVersion = "schemaVersion";
    public const string PropFileExtension = "fileExtension";
    public const string PropRequiredCapabilities = "requiredCapabilities";
    public const string PropInventory = "inventory";
    public const string PropArchiveId = "archiveId";
    public const string PropSnapshotBoundary = "snapshotBoundary";
    public const string PropParts = "parts";
    public const string PropPayloadCounts = "payloadCounts";
    public const string PropDigests = "digests";
    public const string PropOmissions = "omissions";
    public const string PropDependencyReport = "dependencyReport";
    public const string PropPublicKeyCatalog = "publicKeyCatalog";
    public const string PropEncryptedRecords = "encryptedRecords";
    public const string PropPublicKeys = "publicKeys";

    public static string? LegacyRejectReason(int schemaVersion)
    {
        if (CoreSchema.CanRead(schemaVersion))
            return null;
        return $"Schema version {schemaVersion} is not compatible. {CoreSchema.GetCompatibilityInfo()}.";
    }
}

public sealed class KnowzArchiveReadResult
{
    public bool Accepted { get; init; }
    public bool WritesPerformed { get; init; }
    public bool MultipartImplemented { get; init; }
    public string? Error { get; init; }
    public string? LegacyReaderRejectReason { get; init; }
    public Guid ArchiveId { get; init; }
    public string? DependencyReport { get; init; }

    public static KnowzArchiveReadResult Reject(string error) => new()
    {
        Accepted = false,
        WritesPerformed = false,
        MultipartImplemented = false,
        Error = error,
    };
}

/// <summary>
/// Writes an inventory stub. It does not emit parts, ciphertext, or private keys.
/// </summary>
public static class KnowzArchiveProducer
{
    public static string WriteStub(Guid archiveId, string snapshotBoundary, int encryptedRecords, int publicKeys)
    {
        if (archiveId == Guid.Empty)
            throw new ArgumentException("Archive id is required.", nameof(archiveId));
        if (string.IsNullOrWhiteSpace(snapshotBoundary))
            throw new ArgumentException("Snapshot boundary is required.", nameof(snapshotBoundary));
        if (encryptedRecords < 0 || publicKeys < 0)
            throw new ArgumentOutOfRangeException(nameof(encryptedRecords));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(KnowzArchiveProfile.PropFormatId, KnowzArchiveProfile.FormatId);
            writer.WriteNumber(KnowzArchiveProfile.PropFormatVersion, KnowzArchiveProfile.FormatVersion);
            writer.WriteNumber(KnowzArchiveProfile.PropSchemaVersion, KnowzArchiveProfile.SchemaVersion);
            writer.WriteString(KnowzArchiveProfile.PropFileExtension, KnowzArchiveProfile.FileExtension);
            writer.WritePropertyName(KnowzArchiveProfile.PropRequiredCapabilities);
            writer.WriteStartArray();
            writer.WriteStringValue(KnowzArchiveProfile.Capability);
            writer.WriteEndArray();

            writer.WritePropertyName(KnowzArchiveProfile.PropInventory);
            writer.WriteStartObject();
            writer.WriteString(KnowzArchiveProfile.PropArchiveId, archiveId);
            writer.WriteString(KnowzArchiveProfile.PropSnapshotBoundary, snapshotBoundary);
            writer.WritePropertyName(KnowzArchiveProfile.PropParts);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName(KnowzArchiveProfile.PropPayloadCounts);
            writer.WriteStartObject();
            writer.WriteNumber(KnowzArchiveProfile.PropEncryptedRecords, encryptedRecords);
            writer.WriteNumber(KnowzArchiveProfile.PropPublicKeys, publicKeys);
            writer.WriteEndObject();
            writer.WritePropertyName(KnowzArchiveProfile.PropDigests);
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName(KnowzArchiveProfile.PropOmissions);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteString(KnowzArchiveProfile.PropDependencyReport, KnowzArchiveProfile.MultipartNotProduced);
            writer.WriteEndObject();

            writer.WritePropertyName(KnowzArchiveProfile.PropPublicKeyCatalog);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName(KnowzArchiveProfile.PropEncryptedRecords);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Reads an inventory stub and refuses to write. Old plaintext readers reject schema 4.
/// Non-empty part lists are rejected until multipart authentication exists.
/// </summary>
public static class KnowzArchiveConsumer
{
    public static KnowzArchiveReadResult Read(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return KnowzArchiveReadResult.Reject("Archive document is not JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return KnowzArchiveReadResult.Reject("Archive document is not an object.");
            if (SealedMaterialGuard.DeclaresPrivateMaterial(json))
                return KnowzArchiveReadResult.Reject("Private key material is not accepted.");

            if (!root.TryGetProperty(KnowzArchiveProfile.PropFormatId, out var formatId)
                || formatId.GetString() != KnowzArchiveProfile.FormatId)
            {
                return KnowzArchiveReadResult.Reject("Archive formatId is not knowz.archive.");
            }

            if (!root.TryGetProperty(KnowzArchiveProfile.PropFormatVersion, out var formatVersion)
                || formatVersion.ValueKind != JsonValueKind.Number
                || formatVersion.GetInt32() != KnowzArchiveProfile.FormatVersion)
            {
                return KnowzArchiveReadResult.Reject("Archive formatVersion is not 1.");
            }

            if (!root.TryGetProperty(KnowzArchiveProfile.PropSchemaVersion, out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number)
            {
                return KnowzArchiveReadResult.Reject("Archive schemaVersion is missing.");
            }

            var schemaVersion = schemaElement.GetInt32();
            if (schemaVersion != KnowzArchiveProfile.SchemaVersion)
            {
                return KnowzArchiveReadResult.Reject(
                    schemaVersion == EncryptedPortabilityProfile.SchemaVersion
                        ? "Sealed sync packages are not .knowzarchive documents."
                        : "Document schema is not the .knowzarchive profile.");
            }

            if (!TryReadCapabilities(root, out var capabilityError))
                return KnowzArchiveReadResult.Reject(capabilityError!);

            if (!root.TryGetProperty(KnowzArchiveProfile.PropInventory, out var inventory)
                || inventory.ValueKind != JsonValueKind.Object)
            {
                return KnowzArchiveReadResult.Reject("Archive inventory is missing.");
            }

            if (!inventory.TryGetProperty(KnowzArchiveProfile.PropArchiveId, out var archiveIdElement)
                || !Guid.TryParse(archiveIdElement.GetString(), out var archiveId)
                || archiveId == Guid.Empty)
            {
                return KnowzArchiveReadResult.Reject("Archive inventory archiveId is missing.");
            }

            if (!inventory.TryGetProperty(KnowzArchiveProfile.PropParts, out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                return KnowzArchiveReadResult.Reject("Archive inventory parts array is missing.");
            }

            if (parts.GetArrayLength() > 0)
                return KnowzArchiveReadResult.Reject("Multipart archives are not accepted by this stub reader.");

            if (!inventory.TryGetProperty(KnowzArchiveProfile.PropDependencyReport, out var dependency)
                || dependency.GetString() != KnowzArchiveProfile.MultipartNotProduced)
            {
                return KnowzArchiveReadResult.Reject("Archive dependency report is not the stub profile.");
            }

            var legacy = KnowzArchiveProfile.LegacyRejectReason(schemaVersion);
            if (legacy is null)
                return KnowzArchiveReadResult.Reject("Old readers would accept this schema; archive profile refused.");

            return new KnowzArchiveReadResult
            {
                Accepted = true,
                WritesPerformed = false,
                MultipartImplemented = false,
                LegacyReaderRejectReason = legacy,
                ArchiveId = archiveId,
                DependencyReport = KnowzArchiveProfile.MultipartNotProduced,
            };
        }
    }

    private static bool TryReadCapabilities(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(KnowzArchiveProfile.PropRequiredCapabilities, out var caps)
            || caps.ValueKind != JsonValueKind.Array
            || caps.GetArrayLength() != 1)
        {
            error = "Archive is missing capability knowz.archive/v1.";
            return false;
        }

        var only = caps[0].GetString();
        if (!string.Equals(only, KnowzArchiveProfile.Capability, StringComparison.Ordinal))
        {
            error = "Archive is missing capability knowz.archive/v1.";
            return false;
        }

        return true;
    }
}
