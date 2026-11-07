using Knowz.Core.Schema;

namespace Knowz.Core.Portability;

/// <summary>
/// Versioned capability for byte-faithful encrypted vault sync.
/// Plaintext packages stay on <see cref="CoreSchema.Version"/>.
/// Sealed packages use <see cref="SchemaVersion"/>, which readers that only
/// understand <see cref="CoreSchema.CanRead"/> reject before any write.
/// </summary>
public static class EncryptedPortabilityProfile
{
    public const string Capability = "knowz.encrypted-sealed/v1";
    public const int SchemaVersion = 3;

    public const string ReasonIncomplete = "encrypted-incomplete";
    public const string ReasonPending = "encrypted-pending";
    public const string ReasonComment = "comment-not-sealed";
    public const string ReasonFile = "file-not-sealed";
    public const string ReasonFileCap = "file-exceeds-inline-cap";
    public const string ReasonFileBytes = "file-bytes-unavailable";
    public const string ReasonVersion = "version-envelope-missing";
    public const string ReasonCatalog = "public-key-catalog-missing";

    public static bool IsSealedSchema(int schemaVersion) => schemaVersion == SchemaVersion;

    /// <summary>
    /// Returns a failure reason when this build must not write the package.
    /// A sealed package is accepted only with exactly <see cref="Capability"/>.
    /// </summary>
    public static string? RejectReason(PortableExportPackage package)
    {
        if (EncryptedGraphProfile.IsGraphSchema(package.SchemaVersion))
            return EncryptedGraphProfile.Validate(package, requireComplete: package.PreservationState == "complete");
        var caps = package.RequiredCapabilities ?? new List<string>();
        if (IsSealedSchema(package.SchemaVersion))
        {
            if (caps.Count != 1 || !string.Equals(caps[0], Capability, StringComparison.Ordinal))
            {
                return "Encrypted sealed package is missing the required capability knowz.encrypted-sealed/v1.";
            }

            return null;
        }

        if (caps.Count > 0)
        {
            return $"Package requires capabilities this reader does not grant on schema {package.SchemaVersion}.";
        }

        if (!CoreSchema.CanRead(package.SchemaVersion))
        {
            return $"Schema version {package.SchemaVersion} is not compatible. {CoreSchema.GetCompatibilityInfo()}.";
        }

        return null;
    }

    public static string? ValidateSealedGraph(PortableExportPackage package)
    {
        if (EncryptedGraphProfile.IsGraphSchema(package.SchemaVersion))
            return EncryptedGraphProfile.Validate(package, requireComplete: package.PreservationState == "complete");
        if (!IsSealedSchema(package.SchemaVersion))
            return null;

        foreach (var item in package.SealedItems ?? new List<SealedKnowledgeItem>())
        {
            if (!IsCompleteItem(item))
                return $"Sealed item {item.Id} is incomplete; import refused.";
        }

        foreach (var file in package.SealedFiles ?? new List<SealedFileRecord>())
        {
            if (!IsCompleteFile(file))
                return $"Sealed file {file.Id} is incomplete; import refused.";
        }

        foreach (var comment in package.SealedComments ?? new List<SealedCommentRecord>())
        {
            if (!IsCompleteComment(comment))
                return $"Sealed comment {comment.Id} is incomplete; import refused.";
        }

        foreach (var version in package.SealedVersions ?? new List<SealedKnowledgeVersionRecord>())
        {
            if (string.IsNullOrWhiteSpace(version.Ciphertext)
                || string.IsNullOrWhiteSpace(version.EnvelopeMetadata)
                || !version.EnvelopeMetadata.Contains("\"sealedSnapshot\":true", StringComparison.Ordinal))
            {
                return $"Sealed version {version.Id} is incomplete; import refused.";
            }
        }

        return null;
    }

    public static bool IsCompleteItem(SealedKnowledgeItem item) =>
        !string.IsNullOrWhiteSpace(item.Ciphertext)
        && item.EnvelopeVersion is null or 1 or 2
        && (HasCustomer(item.EncryptedDek, item.EncryptedDekNonce, item.EphemeralPublicKey, item.ContentNonce)
            || HasPlatform(item.WrappedDek, item.DekNonce, item.CryptoVersion, item.ContentNonce));

    public static bool IsCompleteFile(SealedFileRecord file) =>
        !string.IsNullOrWhiteSpace(file.BinaryContentBase64)
        && file.EnvelopeVersion is null or 1 or 2
        && HasCustomer(file.EncryptedDek, file.EncryptedDekNonce, file.EphemeralPublicKey, file.ContentNonce);

    public static bool IsCompleteComment(SealedCommentRecord comment) =>
        !string.IsNullOrWhiteSpace(comment.Ciphertext)
        && comment.EnvelopeVersion is null or 1 or 2
        && HasCustomer(comment.EncryptedDek, comment.EncryptedDekNonce, comment.EphemeralPublicKey, comment.ContentNonce);

    private static bool HasCustomer(string? dek, string? dekNonce, string? ephemeral, string? contentNonce) =>
        !string.IsNullOrWhiteSpace(dek)
        && !string.IsNullOrWhiteSpace(dekNonce)
        && !string.IsNullOrWhiteSpace(ephemeral)
        && !string.IsNullOrWhiteSpace(contentNonce);

    private static bool HasPlatform(string? wrappedDek, string? dekNonce, string? cryptoVersion, string? contentNonce) =>
        !string.IsNullOrWhiteSpace(wrappedDek)
        && !string.IsNullOrWhiteSpace(dekNonce)
        && !string.IsNullOrWhiteSpace(cryptoVersion)
        && !string.IsNullOrWhiteSpace(contentNonce);
}

public sealed class VaultSyncExportOptions
{
    public IReadOnlyCollection<string> AcceptedCapabilities { get; init; } = Array.Empty<string>();

    public bool AcceptsGraphPackages => AcceptedCapabilities.Contains(EncryptedGraphProfile.Capability);

    public bool AcceptsSealedPackages => AcceptsGraphPackages
        || AcceptedCapabilities.Any(c => string.Equals(c, EncryptedPortabilityProfile.Capability, StringComparison.Ordinal));

    public static VaultSyncExportOptions FromAcceptList(string? headerOrQuery)
    {
        if (string.IsNullOrWhiteSpace(headerOrQuery))
            return new VaultSyncExportOptions();

        var parts = headerOrQuery.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new VaultSyncExportOptions { AcceptedCapabilities = parts };
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ExportedCryptoContext
{
    public Guid ContextId { get; set; }
    public string BindingKind { get; set; } = "vault";
    public Guid OriginalBindingId { get; set; }
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public string ProvenanceStatus { get; set; } = "archive-unverified";
    public Guid? SourceTenantId { get; set; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ExportedCryptoKeyIdentity
{
    public Guid ContextId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string KeyType { get; set; } = "x25519";
    public int Generation { get; set; } = 1;
    public string Lifecycle { get; set; } = "decrypt-only";
    public string Custody { get; set; } = "customer-held-public-only";
    public string? SourceLifecycle { get; set; }
}

public sealed class SealedKnowledgeItem
{
    public Guid Id { get; set; }
    public List<Guid> VaultIds { get; set; } = [];
    public Guid? PrimaryVaultId { get; set; }
    public Guid? TopicId { get; set; }
    public List<Guid> TagIds { get; set; } = [];
    public List<Guid> PersonIds { get; set; } = [];
    public List<Guid> LocationIds { get; set; } = [];
    public List<Guid> EventIds { get; set; } = [];
    public List<PortableEntityLink>? PersonLinks { get; set; }
    public List<PortableEntityLink>? LocationLinks { get; set; }
    public List<PortableEntityLink>? EventLinks { get; set; }
    public List<PortableKnowledgeRelationship>? Relationships { get; set; }
    public Knowz.Core.Enums.KnowledgeType Type { get; set; } = Knowz.Core.Enums.KnowledgeType.Note;
    public Dictionary<string, System.Text.Json.JsonElement>? SafeMetadata { get; set; }

    public Guid OriginalItemId { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public int? EnvelopeVersion { get; set; }
    public string? EncryptedDek { get; set; }
    public string? EncryptedDekNonce { get; set; }
    public string? EphemeralPublicKey { get; set; }
    public string? ContentNonce { get; set; }
    public string? EncryptionKeyRef { get; set; }
    public string? EncryptedFieldsJson { get; set; }
    public string? WrappedDek { get; set; }
    public string? DekNonce { get; set; }
    public string? CryptoVersion { get; set; }
    public Guid CryptoBindingId { get; set; }
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SealedKnowledgeVersionRecord
{
    public Guid Id { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
    public long VersionNumber { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public string EnvelopeMetadata { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SealedFileRecord
{
    /// <summary>Logical owning vault, independently authorized from the original crypto binding.</summary>
    public Guid? VaultId { get; set; }
    public int FileVersion { get; set; } = 1;
    public Guid? RootFileRecordId { get; set; }
    public Guid? PreviousFileRecordId { get; set; }
    public bool IsCurrentVersion { get; set; } = true;
    public Guid Id { get; set; }
    public Guid OriginalFileId { get; set; }
    public string? BinaryEntryPath { get; set; }
    public string? BinarySha256 { get; set; }
    public long? OriginalSizeBytes { get; set; }
    public Guid? KnowledgeId { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? BinaryContentBase64 { get; set; }
    public string? EncryptionKeyRef { get; set; }
    public int? EnvelopeVersion { get; set; }
    public string? EncryptedDek { get; set; }
    public string? EncryptedDekNonce { get; set; }
    public string? EphemeralPublicKey { get; set; }
    public string? ContentNonce { get; set; }
    public string? EncryptedFieldsJson { get; set; }
    public Guid CryptoBindingId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SealedCommentRecord
{
    public Guid Id { get; set; }
    public Guid OriginalCommentId { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public Guid? OriginalParentCommentId { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public int? EnvelopeVersion { get; set; }
    public string? EncryptedDek { get; set; }
    public string? EncryptedDekNonce { get; set; }
    public string? EphemeralPublicKey { get; set; }
    public string? ContentNonce { get; set; }
    public string? EncryptionKeyRef { get; set; }
    public string? EncryptedFieldsJson { get; set; }
    public Guid CryptoBindingId { get; set; }
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PortableOmission
{
    public string OwnerKind { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
