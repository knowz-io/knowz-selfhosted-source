namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Destination-owned crypto context. Storage vault ids are not this id.
/// Import never marks the context as the primary write key.
/// </summary>
public class ShPortableCryptoContext
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string BindingKind { get; set; } = "vault";
    public Guid OriginalBindingId { get; set; }
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public string ProvenanceStatus { get; set; } = "archive-unverified";
    public Guid? SourceTenantId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ShVaultCryptoContextLink
{
    public int? PrimaryWriteGeneration { get; set; }
    public string? PrimaryWriteFingerprint { get; set; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DestinationVaultId { get; set; }
    public Guid PortableCryptoContextId { get; set; }
    public bool IsPrimaryWriteContext { get; set; }
}

public class ShCryptoKeyIdentity
{
    public string SourceLifecycle { get; set; } = "decrypt-only";
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PortableCryptoContextId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string KeyType { get; set; } = "x25519";
    public int Generation { get; set; } = 1;
    /// <summary>Imported rows stay decrypt-only. Active promotion is refused.</summary>
    public string Lifecycle { get; set; } = "decrypt-only";
    public string Custody { get; set; } = "customer-held-public-only";
}

/// <summary>
/// Sealed knowledge envelope. Ciphertext is stored here, never in Knowledge.Content.
/// LifecycleStatus is preserved-locked or client-verified. The server does not decrypt.
/// </summary>
public class ShSealedKnowledge
{
    public bool IsDeleted { get; set; }
    public string LineageScope { get; set; } = "";
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
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
    public string LifecycleStatus { get; set; } = "preserved-locked";
    public string? VerifiedFingerprint { get; set; }
    public DateTime? ClientVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ShSealedVersion
{
    public bool IsDeleted { get; set; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
    public long VersionNumber { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public string EnvelopeMetadata { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ShSealedFile
{
    public long? OriginalSizeBytes { get; set; }
    public bool IsDeleted { get; set; }
    public int FileVersion { get; set; } = 1;
    public Guid? RootFileRecordId { get; set; }
    public Guid? PreviousFileRecordId { get; set; }
    public bool IsCurrentVersion { get; set; } = true;
    public Guid OriginalFileId { get; set; }
    public Guid? BlobStorageId { get; set; }
    public string? BinarySha256 { get; set; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? VaultId { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Sealed comment envelope. Ciphertext is stored here; plaintext comment body stays empty.
/// Comments on sealed items are sealed-only; no plaintext KnowledgeComment row is created.
/// </summary>
public class ShSealedComment
{
    public bool IsDeleted { get; set; }
    public string LineageScope { get; set; } = "";
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalCommentId { get; set; }
    public Guid? ParentCommentId { get; set; }
    public Guid? OriginalParentCommentId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Physical objects are immutable stages; logical identity survives replacement.</summary>
public class ShFileObjectReference
{
    public Guid FileRecordId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BlobStorageId { get; set; }
    public string Sha256 { get; set; } = "";
}

public class ShPayloadKeyReference
{
    public bool IsDeleted { get; set; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string OwnerKind { get; set; } = "knowledge";
    public Guid OwnerId { get; set; }
    public string Role { get; set; } = "body";
    public string? FieldName { get; set; }
    public Guid? VersionId { get; set; }
    public Guid ContextId { get; set; }
    public string? Fingerprint { get; set; }
    public bool LegacyUnattributed { get; set; }
    public int? Generation { get; set; }
    public string OriginalBindingKind { get; set; } = "vault";
    public Guid OriginalBindingId { get; set; }
    public Guid? OriginalItemId { get; set; }
    public string KeyType { get; set; } = "x25519-vault-public";
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public int EnvelopeVersion { get; set; } = 1;
}

public class ShSealedAttachment
{
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid OriginalAttachmentId { get; set; }
    public Guid FileRecordId { get; set; }
    public Guid? KnowledgeId { get; set; }
    public Guid? CommentId { get; set; }
    public Guid? InboxId { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPrimary { get; set; }
    public string? EncryptedFieldsJson { get; set; }
    public bool ExcludeFromAiProcessing { get; set; }
    public bool ExcludeFromVersionDiff { get; set; }
    public DateTime AttachedAt { get; set; }
}

/// <summary>Service signing key protected by installation Data Protection; never a customer decryption key.</summary>
public class ShArchiveSigningIdentity
{
    public Guid TenantId { get; set; }
    public string ProtectedPrivateKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Durable verified-stage ownership and commit recovery. Contains object IDs, never archive plaintext or customer keys.</summary>
public class ShArchiveImportJournal
{
    public Guid PlanId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorNamespace { get; set; } = "";
    public string? PeerBindingSha256 { get; set; }
    public Guid ArchiveId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string MappingSha256 { get; set; } = "";
    public string Status { get; set; } = "preparing";
    public string StagedObjectIdsJson { get; set; } = "[]";
    public string? ImportOptionsJson { get; set; }
    public string? CommitReceiptJson { get; set; }
    public Guid? CommitId { get; set; }
    public long PreservedPayloadCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Private upload intent; never part of the live attachment graph until authenticated bytes commit.</summary>
public class ShSealedFileStage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public Guid VaultId { get; set; }
    public Guid FileId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Revision { get; set; } = "";
    public string MetadataJson { get; set; } = "";
    public string Status { get; set; } = "prepared";
    public Guid? BlobStorageId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
