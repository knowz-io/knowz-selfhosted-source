using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knowz.Core.Portability;

/// <summary>Public inert history. Import never creates active release/recipient/token authority from this record.</summary>
public sealed class PortableSuspendedReleasePolicy
{
    public int FormatVersion { get; set; } = 1;
    public string ActivationState { get; set; } = "suspended";
    public Guid OriginalLockId { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
    public Guid SourceTenantId { get; set; }
    public Guid OriginalOwnerUserId { get; set; }
    public bool OriginalIsSealed { get; set; }
    public string OriginalStatus { get; set; } = "Sealed";
    public string SecurityLevel { get; set; } = "AccessControl";
    public int GracePeriodHours { get; set; } = 48;
    public DateTime? GracePeriodEndsAt { get; set; }
    public DateTime? ConditionMetAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public bool ExcludeAiWhileSealed { get; set; }
    public bool DecryptOnFetch { get; set; }
    public bool EncryptedDeliveryEnabled { get; set; }
    public DateTime? EncryptedCopySentAt { get; set; }
    public DateTime? RecipientKeyDeliveredAt { get; set; }
    public Guid? OriginalCollectionId { get; set; }
    public Guid? OriginalCollectionVaultId { get; set; }
    public string? CollectionName { get; set; }
    public string? CollectionDescription { get; set; }
    public List<PortableReleaseCondition> Conditions { get; set; } = [];
    public List<PortableReleaseRecipientState> Recipients { get; set; } = [];
    public List<PortableReleaseCustodyDependency> CustodyDependencies { get; set; } = [];
    public string? RecipientEnvelopeJson { get; set; }
}
public sealed class PortableReleaseCondition
{
    public Guid OriginalConditionId { get; set; }
    public Guid? OriginalParentConditionId { get; set; }
    public string Type { get; set; } = "Time";
    public string Status { get; set; } = "Pending";
    public DateTime? ReleaseAt { get; set; }
    public Guid? OriginalPersonId { get; set; }
    public Guid? OriginalTrustCircleId { get; set; }
    public int? RelativeOffsetDays { get; set; }
    public int? RequiredConfirmations { get; set; }
    public int CurrentConfirmations { get; set; }
    public string? ConditionDescription { get; set; }
    public Guid? OriginalDesignatedUserId { get; set; }
    public string? CompositeOperator { get; set; }
    public DateTime? LastEvaluatedAt { get; set; }
    public string? EvaluationNotes { get; set; }
}
public sealed class PortableReleaseRecipientState
{
    public Guid OriginalRecipientId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string? RecipientName { get; set; }
    public Guid? OriginalPersonId { get; set; }
    public Guid? OriginalIdentityId { get; set; }
    public Guid? OriginalIdentityNoteKeyId { get; set; }
    public string? IdentityNoteKeyFingerprint { get; set; }
    public int? AccessExpiresAfterHours { get; set; }
    public DateTime? OriginalTokenExpiresAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public DateTime? AccessedAt { get; set; }
}
public sealed class PortableReleaseCustodyDependency
{
    public string Profile { get; set; } = "tenant-content-release";
    public Guid SourceTenantId { get; set; }
    public string? SourceKeyRef { get; set; }
    public string RecoveryState { get; set; } = "source-provider-required";
    public Guid? OriginalItemBindingId { get; set; }
    public string? PublicKeyFingerprint { get; set; }
    public string? RecipientPublicKey { get; set; }
}
/// <summary>Actual platform AES-GCM bytes, deliberately distinct from an X25519 envelope.</summary>
public sealed class PortableDependencyBoundKnowledge
{
    public int FormatVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public Guid OriginalItemId { get; set; }
    public Guid SourceTenantId { get; set; }
    public Guid OriginalOwnerUserId { get; set; }
    public bool OriginalIsSealed { get; set; }
    public List<Guid> VaultIds { get; set; } = [];
    public Guid? PrimaryVaultId { get; set; }
    public string AlgorithmProfile { get; set; } = "platform-managed-v1";
    public string Ciphertext { get; set; } = "";
    public string ContentNonce { get; set; } = "";
    public string? EncryptedFieldsJson { get; set; }
    public PortableReleaseCustodyDependency Custody { get; set; } = new();
    public PortableKnowledge? PublicRecord { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PortableDependencyBoundVersion> Versions { get; set; } = [];
}
public sealed class PortableDependencyBoundVersion
{
    public int FormatVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public Guid KnowledgeId { get; set; }
    public Guid OriginalKnowledgeId { get; set; }
    public long VersionNumber { get; set; }
    public Guid SourceTenantId { get; set; }
    public string AlgorithmProfile { get; set; } = "platform-managed-v1";
    public string Ciphertext { get; set; } = "";
    public string ContentNonce { get; set; } = "";
    public string? EncryptedFieldsJson { get; set; }
    public PortableReleaseCustodyDependency Custody { get; set; } = new();
    public string OriginalEnvelopeMetadata { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class SuspendedReleaseProfile
{
    public const string Capability = "knowz.suspended-release/v1";
    public const int MaxPolicyBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    public static PortableSuspendedReleasePolicy ParsePolicy(byte[] raw)
    {
        try
        {
            using var document = StrictJson.Parse(raw, MaxPolicyBytes);
            var value = document.RootElement.Deserialize<PortableSuspendedReleasePolicy>(Json) ?? throw new JsonException();
            var problem = Validate(value); if (problem != null) throw new KnowzArchiveException(problem); return value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw new KnowzArchiveException("Invalid suspended release policy.", ex); }
    }
    public static string? Validate(PortableSuspendedReleasePolicy value)
    {
        if (value == null || value.Conditions == null || value.Recipients == null || value.CustodyDependencies == null
            || value.Conditions.Any(c => c == null) || value.Recipients.Any(r => r == null) || value.CustodyDependencies.Any(d => d == null))
            return "Suspended release policy has null required collections or children.";
        if (value.FormatVersion != 1 || value.ActivationState != "suspended" || value.OriginalLockId == Guid.Empty || value.KnowledgeId == Guid.Empty
            || value.OriginalKnowledgeId == Guid.Empty || value.SourceTenantId == Guid.Empty || value.OriginalOwnerUserId == Guid.Empty)
            return "Suspended release policy lacks immutable identity or supported inactive version.";
        if (value.OriginalStatus is not ("Sealed" or "ConditionMet" or "InGracePeriod" or "Released" or "Cancelled" or "Expired" or "Revoked")
            || value.SecurityLevel is not ("AccessControl" or "Encrypted" or "PlatformManaged") || value.GracePeriodHours < 0 || value.GracePeriodHours > 87600
            || value.Conditions.Count > 1024 || value.Recipients.Count > 1024 || value.CustodyDependencies.Count > 256)
            return "Unsupported suspended release state or bounds.";
        if (value.Conditions.Any(c => c.OriginalConditionId == Guid.Empty) || value.Conditions.Select(c => c.OriginalConditionId).Distinct().Count() != value.Conditions.Count
            || value.Recipients.Any(r => r.OriginalRecipientId == Guid.Empty) || value.Recipients.Select(r => r.OriginalRecipientId).Distinct().Count() != value.Recipients.Count)
            return "Duplicate or empty original policy child identities.";
        var conditions = value.Conditions.ToDictionary(c => c.OriginalConditionId);
        foreach (var condition in value.Conditions)
        {
            if (condition.Type is not ("Time" or "Death" or "CircleQuorum" or "AIEvent" or "Manual" or "Composite")
                || condition.Status is not ("Pending" or "Met" or "Failed" or "Cancelled") || condition.CompositeOperator is not (null or "And" or "Or" or "AND" or "OR")
                || condition.CurrentConfirmations < 0 || condition.RequiredConfirmations < 0) return "Unsupported release condition declaration.";
            var seen = new HashSet<Guid> { condition.OriginalConditionId }; var current = condition;
            while (current.OriginalParentConditionId is { } parent)
            { if (!seen.Add(parent) || !conditions.TryGetValue(parent, out current!)) return "Release condition parent is absent or cyclic."; }
        }
        foreach (var recipient in value.Recipients)
            if (string.IsNullOrWhiteSpace(recipient.RecipientEmail) || recipient.RecipientEmail.Length > 320 || recipient.AccessExpiresAfterHours < 0
                || recipient.IdentityNoteKeyFingerprint != null && !EncryptedGraphProfile.IsSha256(recipient.IdentityNoteKeyFingerprint)) return "Invalid public recipient history.";
        foreach (var dependency in value.CustodyDependencies) { var error = Validate(dependency); if (error != null) return error; }
        if (value.RecipientEnvelopeJson != null)
        {
            var error = ValidateRecipientEnvelope(value); if (error != null) return error;
        }
        return null;
    }
    public static string? Validate(PortableDependencyBoundKnowledge value)
    {
        if (value == null || value.Custody == null || value.VaultIds == null || value.Versions == null || value.Versions.Count > 10000 || value.Versions.Any(v => v == null)) return "Dependency-bound payload lacks required custody or graph declarations.";
        if (value.FormatVersion != 1 || value.Id == Guid.Empty || value.OriginalItemId == Guid.Empty || value.SourceTenantId == Guid.Empty
            || value.AlgorithmProfile != "platform-managed-v1" || !Base64(value.Ciphertext, 16, 8 * 1024 * 1024) || !Base64(value.ContentNonce, 12, 12)
            || value.Custody.Profile != "tenant-content-release" || value.Custody.SourceTenantId != value.SourceTenantId)
            return "Dependency-bound platform payload has an unsupported algorithm, version, identity or envelope length.";
        if (value.PublicRecord != null && (value.PublicRecord.Id != value.Id || !string.IsNullOrEmpty(value.PublicRecord.Content))) return "Dependency-bound ciphertext must not masquerade as plaintext content.";
        if (value.Versions.Select(v => v.Id).Distinct().Count() != value.Versions.Count) return "Dependency-bound history contains duplicate version identities.";
        foreach (var version in value.Versions)
        {
            if (version.KnowledgeId != value.Id || version.OriginalKnowledgeId != value.OriginalItemId) return "Dependency-bound history has a mismatched owner.";
            var problem = Validate(version); if (problem != null) return problem;
        }
        return Validate(value.Custody);
    }
    public static string? Validate(PortableDependencyBoundVersion version)
    {
        if (version == null || version.FormatVersion != 1 || version.Id == Guid.Empty || version.KnowledgeId == Guid.Empty || version.OriginalKnowledgeId == Guid.Empty
            || version.VersionNumber < 1 || version.SourceTenantId == Guid.Empty || version.AlgorithmProfile != "platform-managed-v1" || version.Custody == null
            || version.Custody.SourceTenantId != version.SourceTenantId || version.Custody.Profile != "tenant-content-release" || !Base64(version.Ciphertext, 16, 8 * 1024 * 1024) || !Base64(version.ContentNonce, 12, 12))
            return "Dependency-bound version has an invalid algorithm, identity or envelope.";
        var custodyError = Validate(version.Custody); if (custodyError != null) return custodyError;
        try
        {
            using var doc = StrictJson.Parse(version.OriginalEnvelopeMetadata, MaxPolicyBytes); var metadata = doc.RootElement;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "sealedSnapshot", "encryptionEnvelopeVersion", "encryptedDek", "encryptedDekNonce", "ephemeralPublicKey", "contentNonce", "encryptionKeyRef", "encryptedFieldsJson", "cryptoBindingVaultId", "wrappedDek", "dekNonce", "cryptoVersion" };
            if (metadata.ValueKind != JsonValueKind.Object || metadata.EnumerateObject().Any(p => !allowed.Contains(p.Name))
                || !metadata.TryGetProperty("sealedSnapshot", out var flag) || flag.ValueKind != JsonValueKind.True
                || metadata.GetProperty("cryptoVersion").GetString() != "platform-managed-v1" || metadata.GetProperty("contentNonce").GetString() != version.ContentNonce
                || metadata.GetProperty("encryptionKeyRef").GetString() != version.Custody.SourceKeyRef)
                return "Dependency-bound version lacks its exact supported original envelope associations.";
            foreach (var name in new[] { "encryptedDek", "encryptedDekNonce", "ephemeralPublicKey", "wrappedDek", "dekNonce", "encryptionEnvelopeVersion" })
                if (metadata.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) return "Dependency-bound version mixes another key/envelope scheme.";
            if (metadata.TryGetProperty("encryptedFieldsJson", out var fields) && fields.ValueKind != JsonValueKind.Null && fields.GetString() != version.EncryptedFieldsJson)
                return "Dependency-bound version fields differ from the original snapshot.";
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return "Dependency-bound version metadata is malformed or unsupported."; }
    }
    public static string? Validate(PortableReleaseCustodyDependency dependency)
    {
        if (dependency == null) return "Custody dependency is absent.";
        if (dependency.SourceTenantId == Guid.Empty) return "Custody source identity is absent.";
        if (dependency.Profile == "tenant-content-release")
        {
            var prefix = $"tenant:{dependency.SourceTenantId:D}:purpose:content-release:v"; var reference = dependency.SourceKeyRef;
            if (reference == null || !reference.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(reference[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version < 1 || version.ToString(CultureInfo.InvariantCulture) != reference[prefix.Length..] || dependency.RecoveryState != "source-provider-required"
                || dependency.RecipientPublicKey != null) return "Unsupported or foreign platform provider key reference.";
        }
        else if (dependency.Profile == "recipient-item-x25519")
        {
            if (dependency.OriginalItemBindingId is not { } binding || binding == Guid.Empty || !EncryptedGraphProfile.IsSha256(dependency.PublicKeyFingerprint)
                || dependency.RecoveryState is not ("recipient-key-required" or "missing-recipient-public-key")) return "Recipient custody dependency lacks an exact original item binding/fingerprint.";
            if (dependency.RecipientPublicKey != null && (!Base64(dependency.RecipientPublicKey, 32, 32)
                || Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(dependency.RecipientPublicKey))) != dependency.PublicKeyFingerprint)) return "Recipient public key fingerprint differs from its exact bytes.";
            if (dependency.RecipientPublicKey == null && dependency.RecoveryState != "missing-recipient-public-key") return "Missing recipient public key must remain explicit.";
        }
        else return "Unknown custody dependency profile.";
        return null;
    }
    private static string? ValidateRecipientEnvelope(PortableSuspendedReleasePolicy policy)
    {
        try
        {
            using var doc = StrictJson.Parse(policy.RecipientEnvelopeJson!, MaxPolicyBytes); var root = doc.RootElement;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "format", "version", "alg", "binding", "contentAadBound", "envelope", "fingerprint", "createdAt" };
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !allowed.Remove(p.Name)) || allowed.Count != 0
                || root.GetProperty("format").GetString() != "knowz-encrypted-message-v2" || root.GetProperty("version").GetInt32() != 2 || root.GetProperty("contentAadBound").ValueKind != JsonValueKind.True)
                return "Unsupported recipient message version or fields.";
            var binding = root.GetProperty("binding"); var algorithms = root.GetProperty("alg"); var envelope = root.GetProperty("envelope");
            if (!ExactFields(binding, "vaultId", "keyType", "itemId") || !ExactFields(algorithms, "kdf", "keyAgreement", "dekWrap", "content")
                || !ExactFields(envelope, "ephemeralPublicKey", "encryptedDek", "dekNonce", "contentNonce", "ciphertext")) return "Recipient message contains unknown or secret authority fields.";
            if (binding.GetProperty("vaultId").GetString() != policy.OriginalLockId.ToString("D") || binding.GetProperty("itemId").GetString() != policy.OriginalLockId.ToString("D")
                || binding.GetProperty("keyType").GetString() != "x25519-item-public" || algorithms.GetProperty("kdf").GetString() != "hkdf-sha256"
                || algorithms.GetProperty("keyAgreement").GetString() != "x25519" || algorithms.GetProperty("dekWrap").GetString() != "aes-256-gcm" || algorithms.GetProperty("content").GetString() != "aes-256-gcm")
                return "Recipient message has a different original binding or algorithm.";
            if (!Base64(envelope.GetProperty("ciphertext").GetString(), 16, MaxPolicyBytes) || !Base64(envelope.GetProperty("contentNonce").GetString(), 12, 12)
                || !Base64(envelope.GetProperty("encryptedDek").GetString(), 48, 48) || !Base64(envelope.GetProperty("dekNonce").GetString(), 12, 12)
                || !Base64(envelope.GetProperty("ephemeralPublicKey").GetString(), 32, 32)
                || !policy.CustodyDependencies.Any(d => d.Profile == "recipient-item-x25519" && d.OriginalItemBindingId == policy.OriginalLockId && d.PublicKeyFingerprint == root.GetProperty("fingerprint").GetString()))
                return "Recipient encrypted bytes, fingerprint or custody dependency are absent.";
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return "Recipient message metadata is malformed."; }
    }
    private static bool ExactFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false; var expected = names.ToHashSet(StringComparer.Ordinal);
        return !value.EnumerateObject().Any(p => !expected.Remove(p.Name)) && expected.Count == 0;
    }
    private static bool Base64(string? value, int minimum, int maximum)
    {
        if (value == null || value.Length > ((maximum + 2) / 3) * 4) return false;
        try { var bytes = Convert.FromBase64String(value); return bytes.Length >= minimum && bytes.Length <= maximum && Convert.ToBase64String(bytes) == value; } catch (FormatException) { return false; }
    }
}
