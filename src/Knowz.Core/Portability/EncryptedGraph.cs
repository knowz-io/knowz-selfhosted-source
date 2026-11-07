using System.Security.Cryptography;
using System.Text.Json;

namespace Knowz.Core.Portability;

/// <summary>Complete graph negotiation. Legacy schema3 readers must reject this profile.</summary>
public static class EncryptedGraphProfile
{
    public const int SchemaVersion = 5;
    public const string Capability = "knowz.encrypted-graph/v1";
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public static bool IsGraphSchema(int version) => version == SchemaVersion;

    /// <param name="allowDetachedReferences">Trusted existing-record mutation snapshots only, after live
    /// owner authorization. Skips catalog lookup for unchanged relationships, never structural or crypto checks.
    /// Archive readers, writers, imports and new creates must keep the default.</param>
    public static string? Validate(PortableExportPackage package, bool requireComplete = true, bool allowDetachedReferences = false)
    {
        if (package == null || package.Data == null) return "Graph envelope or data is missing.";
        var data = package.Data;
        if (data.Vaults == null || data.KnowledgeItems == null || data.Comments == null || data.FileRecords == null
            || data.Topics == null || data.Tags == null || data.Persons == null || data.Locations == null
            || data.Events == null || data.InboxItems == null
            || HasNull(data.Vaults) || HasNull(data.KnowledgeItems) || HasNull(data.Comments) || HasNull(data.FileRecords)
            || HasNull(data.Topics) || HasNull(data.Tags) || HasNull(data.Persons) || HasNull(data.Locations)
            || HasNull(data.Events) || HasNull(data.InboxItems)
            || HasNull(package.CryptoContexts) || HasNull(package.CryptoKeyCatalog) || HasNull(package.SealedItems)
            || HasNull(package.SealedComments) || HasNull(package.SealedFiles) || HasNull(package.SealedAttachments)
            || HasNull(package.SealedVersions) || HasNull(package.PlainVersions) || HasNull(package.PayloadKeyReferences)
            || HasNull(package.VaultCryptoContextLinks) || HasNull(package.SuspendedReleasePolicies)
            || HasNull(package.DependencyBoundItems) || HasNull(package.Omissions)
            || data.FileRecords.Any(f => HasNull(f.Attachments)))
            return "Graph contains missing collections or null records.";
        if (package.SchemaVersion != SchemaVersion)
            return "Package is not the complete encrypted graph schema.";
        var dependencyItems = package.DependencyBoundItems ?? [];
        var policies = package.SuspendedReleasePolicies ?? [];
        var hasSuspendedData = dependencyItems.Count > 0 || policies.Count > 0;
        if (package.RequiredCapabilities == null || package.RequiredCapabilities.Count != (hasSuspendedData ? 2 : 1)
            || !package.RequiredCapabilities.Contains(Capability)
            || hasSuspendedData && !package.RequiredCapabilities.Contains(SuspendedReleaseProfile.Capability)
            || package.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() != package.RequiredCapabilities.Count)
            return "Complete graph capability is required.";
        if (requireComplete && package.Omissions is { Count: > 0 })
            return "Complete graph contains omissions.";

        if (CheckRelationshipClosure(package, allowDetachedReferences) is { } relationshipError) return relationshipError;
        var contexts = package.CryptoContexts ?? [];
        var keys = package.CryptoKeyCatalog ?? [];
        var items = package.SealedItems ?? [];
        var comments = package.SealedComments ?? [];
        var files = package.SealedFiles ?? [];
        var attachments = package.SealedAttachments ?? [];
        var versions = package.SealedVersions ?? [];
        var refs = package.PayloadKeyReferences ?? [];
        if (CheckFileLineage(package, requireComplete) is { } lineageError) return lineageError;
        if (Duplicates(contexts.Select(c => c.ContextId)) || Duplicates(items.Select(i => i.Id))
            || Duplicates(comments.Select(c => c.Id)) || Duplicates(files.Select(f => f.Id))
            || Duplicates(attachments.Select(a => a.Id)) || Duplicates(versions.Select(v => v.Id)))
            return "Graph contains empty or duplicate record identities.";
        var contextIds = contexts.Select(c => c.ContextId).ToHashSet();
        var allItemIds = items.Select(i => i.Id).Concat(package.Data.KnowledgeItems.Select(i => i.Id)).Concat(dependencyItems.Select(i => i.Id)).ToList();
        if (Duplicates(allItemIds)) return "Knowledge graph has duplicate or empty owners.";
        var itemIds = allItemIds.ToHashSet();
        var allCommentIds = comments.Select(c => c.Id).Concat(package.Data.Comments.Select(c => c.Id)).ToList();
        if (Duplicates(allCommentIds)) return "Comment graph has duplicate or empty identities.";
        var commentIds = allCommentIds.ToHashSet();
        var fileIds = files.Select(f => f.Id).Concat(package.Data.FileRecords.Select(f => f.Id)).ToHashSet();
        var vaultIds = package.Data.Vaults.Select(v => v.Id).ToHashSet();
        foreach (var dependency in dependencyItems)
        {
            if (SuspendedReleaseProfile.Validate(dependency) is { } problem) return problem;
            if (dependency.VaultIds.Count == 0 || dependency.VaultIds.Any(id => !vaultIds.Contains(id))
                || dependency.PrimaryVaultId.HasValue && !dependency.VaultIds.Contains(dependency.PrimaryVaultId.Value))
                return "Dependency-bound payload references an absent owning vault.";
            var error = CheckFields(dependency.EncryptedFieldsJson, "knowledge", dependency.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
            foreach (var version in dependency.Versions)
            {
                error = CheckFields(version.EncryptedFieldsJson, "version", version.Id, refs, contexts, keys, requireComplete);
                if (error != null) return error;
            }
        }
        var dependencyVersions = dependencyItems.SelectMany(i => i.Versions).ToList();
        if (Duplicates(dependencyVersions.Select(v => v.Id).Concat(versions.Select(v => v.Id)).Concat((package.PlainVersions ?? []).Select(v => v.Id))))
            return "Historical graph has duplicate or empty version identities.";
        if (Duplicates(policies.Select(p => p.OriginalLockId))) return "Duplicate original suspended policy identities.";
        foreach (var policy in policies)
        {
            if (SuspendedReleaseProfile.Validate(policy) is { } problem) return problem;
            if (!itemIds.Contains(policy.KnowledgeId)) return "Suspended policy references an absent knowledge owner.";
            var original = items.FirstOrDefault(i => i.Id == policy.KnowledgeId)?.OriginalItemId
                ?? dependencyItems.FirstOrDefault(i => i.Id == policy.KnowledgeId)?.OriginalItemId;
            if (original.HasValue && original != policy.OriginalKnowledgeId)
                return "Suspended policy contradicts its original knowledge identity.";
        }
        foreach (var context in contexts)
        {
            if (context.OriginalBindingId == Guid.Empty || context.BindingKind is not ("vault" or "release-lock" or "item")
                || context.AlgorithmProfile is not ("x25519-vault" or "x25519-item"))
                return "Invalid immutable crypto context.";
        }
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!contextIds.Contains(key.ContextId) || key.Generation < 1 || key.KeyType is not ("x25519" or "x25519-vault-public" or "x25519-item-public"))
                return "Invalid public key context, generation or type.";
            if (!TryBase64(key.PublicKey, 32, 32, out var bytes) || !IsSha256(key.Fingerprint)
                || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), key.Fingerprint, StringComparison.Ordinal))
                return "Public key fingerprint does not match its exact bytes.";
            if (!seenKeys.Add($"{key.ContextId:D}|{key.Generation}"))
                return "Duplicate public key identity.";
        }
        var links = package.VaultCryptoContextLinks ?? [];
        if (links.GroupBy(l => (l.VaultId, l.ContextId)).Any(g => g.Count() > 1)
            || links.Where(l => l.IsPrimaryWriteContext).GroupBy(l => l.VaultId).Any(g => g.Count() > 1))
            return "Duplicate context links or conflicting primary write contexts.";
        foreach (var link in links)
        {
            if (!vaultIds.Contains(link.VaultId) || !contextIds.Contains(link.ContextId))
                return "Crypto context link references a missing vault or context.";
            if (link.IsPrimaryWriteContext && !keys.Any(k => k.ContextId == link.ContextId && k.Fingerprint == link.PrimaryWriteFingerprint && (!link.PrimaryWriteGeneration.HasValue || k.Generation == link.PrimaryWriteGeneration)))
                return "Primary write link has no exact public key.";
            if (link.IsPrimaryWriteContext && !link.PrimaryWriteGeneration.HasValue
                && keys.Where(k => k.ContextId == link.ContextId && k.Fingerprint == link.PrimaryWriteFingerprint).Select(k => k.Generation).Distinct().Count() != 1)
                return "Primary write generation is ambiguous.";
        }
        foreach (var item in items)
        {
            if (item.OriginalItemId == Guid.Empty || item.CryptoBindingId == Guid.Empty)
                return "Encrypted item is missing its immutable identity.";
            if (!CustomerEnvelope(item.Ciphertext, item.EncryptedDek, item.EncryptedDekNonce, item.EphemeralPublicKey, item.ContentNonce, item.EnvelopeVersion))
                return "Encrypted item envelope is incomplete or unsupported.";
            var error = CheckReference(refs, contexts, keys, "knowledge", item.Id, "body", null, item.CryptoBindingId, item.EnvelopeVersion ?? 1, requireComplete, item.OriginalItemId);
            if (error != null) return error;
            error = CheckCanonicalKeyRef(item.EncryptionKeyRef, refs, keys, "knowledge", item.Id, "body");
            if (error != null) return error;
            error = CheckFields(item.EncryptedFieldsJson, "knowledge", item.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
        }
        foreach (var item in data.KnowledgeItems)
        {
            if (item.OriginalItemId == Guid.Empty) return "Knowledge original identity is empty.";
            var error = CheckFields(item.EncryptedFieldsJson, "knowledge", item.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
            if (HasField(item.EncryptedFieldsJson, "title") && !string.IsNullOrEmpty(item.Title) && item.Title != "Encrypted item"
                || HasField(item.EncryptedFieldsJson, "summary") && !string.IsNullOrEmpty(item.Summary)
                || HasField(item.EncryptedFieldsJson, "content") && !string.IsNullOrEmpty(item.Content)
                || HasField(item.EncryptedFieldsJson, "briefSummary") && !string.IsNullOrEmpty(item.BriefSummary)
                || ProtectedKnowledgeFields.HasPlaintextExtensionCompanion(item))
                return "Protected knowledge fields have plaintext companions.";
        }
        foreach (var comment in comments)
        {
            if (!itemIds.Contains(comment.KnowledgeId) || (comment.ParentCommentId.HasValue && !commentIds.Contains(comment.ParentCommentId.Value)))
                return "Comment references a missing owner or parent.";
            if (!string.IsNullOrEmpty(comment.Ciphertext))
            {
                if (!CustomerEnvelope(comment.Ciphertext, comment.EncryptedDek, comment.EncryptedDekNonce, comment.EphemeralPublicKey, comment.ContentNonce, comment.EnvelopeVersion))
                    return "Comment body envelope is incomplete.";
                var error = CheckReference(refs, contexts, keys, "comment", comment.Id, "body", null, comment.CryptoBindingId, comment.EnvelopeVersion ?? 1, requireComplete);
                if (error != null) return error;
                error = CheckCanonicalKeyRef(comment.EncryptionKeyRef, refs, keys, "comment", comment.Id, "body");
                if (error != null) return error;
            }
            else if (!HasField(comment.EncryptedFieldsJson, "content"))
                return "Comment has neither a body nor an encrypted content field.";
            var fieldError = CheckFields(comment.EncryptedFieldsJson, "comment", comment.Id, refs, contexts, keys, requireComplete);
            if (fieldError != null) return fieldError;
        }
        foreach (var file in files)
        {
            if (file.VaultId.HasValue && !vaultIds.Contains(file.VaultId.Value))
                return "Encrypted file references a missing owning vault.";
            if (file.SizeBytes < 16 || file.SizeBytes > MaxFileBytes || file.OriginalFileId == Guid.Empty)
                return "Invalid encrypted file length or original identity.";
            if (file.BinaryEntryPath != null)
            {
                if (!IsSafePath(file.BinaryEntryPath) || !IsSha256(file.BinarySha256))
                    return "Invalid external binary inventory reference.";
                if (file.BinaryContentBase64 != null) return "File has conflicting inline and external bytes.";
            }
            else if (!TryBase64(file.BinaryContentBase64, 16, MaxFileBytes, out var inline) || inline.LongLength != file.SizeBytes)
                return "Encrypted file bytes are missing or have an incorrect length.";
            if (!EnvelopeParts(file.EncryptedDek, file.EncryptedDekNonce, file.EphemeralPublicKey, file.ContentNonce, file.EnvelopeVersion))
                return "Encrypted file envelope is incomplete.";
            if (file.KnowledgeId.HasValue && !itemIds.Contains(file.KnowledgeId.Value))
                return "File references a missing knowledge owner.";
            var error = CheckReference(refs, contexts, keys, "file", file.Id, "binary", null, file.CryptoBindingId, file.EnvelopeVersion ?? 1, requireComplete);
            if (error != null) return error;
            error = CheckCanonicalKeyRef(file.EncryptionKeyRef, refs, keys, "file", file.Id, "binary");
            if (error != null) return error;
            error = CheckFields(file.EncryptedFieldsJson, "file", file.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
        }
        foreach (var attachment in attachments)
        {
            if (!fileIds.Contains(attachment.FileRecordId)
                || attachment.KnowledgeId.HasValue && !itemIds.Contains(attachment.KnowledgeId.Value)
                || attachment.CommentId.HasValue && !commentIds.Contains(attachment.CommentId.Value)
                || attachment.InboxId.HasValue && !package.Data.InboxItems.Any(i => i.Id == attachment.InboxId)
                || !attachment.KnowledgeId.HasValue && !attachment.CommentId.HasValue && !attachment.InboxId.HasValue)
                return "Attachment references a missing file or owner.";
            if (!string.IsNullOrEmpty(attachment.Title) && HasField(attachment.EncryptedFieldsJson, "title")
                || !string.IsNullOrEmpty(attachment.Description) && HasField(attachment.EncryptedFieldsJson, "description"))
                return "Protected attachment fields have leaked plaintext companions.";
            var error = CheckFields(attachment.EncryptedFieldsJson, "attachment", attachment.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
        }
        foreach (var file in package.Data.FileRecords)
        {
            if (!string.IsNullOrEmpty(file.FileName) && file.FileName != "encrypted-file" && HasField(file.EncryptedFieldsJson, "fileName"))
                return "Protected file name has a plaintext companion.";
            var error = CheckFields(file.EncryptedFieldsJson, "file", file.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
        }
        foreach (var version in versions)
        {
            if (!itemIds.Contains(version.KnowledgeId) || version.VersionNumber < 1 || string.IsNullOrWhiteSpace(version.EnvelopeMetadata))
                return "Version is missing its owner or complete snapshot.";
            try
            {
                using var doc = StrictJson.Parse(version.EnvelopeMetadata, 1024 * 1024);
                var root = doc.RootElement;
                if (!root.TryGetProperty("sealedSnapshot", out var sealedSnapshot) || sealedSnapshot.ValueKind != JsonValueKind.True)
                    return "Version is not a sealed snapshot.";
                var encrypted = !root.TryGetProperty("isEncrypted", out var flag) || flag.ValueKind == JsonValueKind.True;
                var versionNumber = SnapshotEnvelopeVersion(root);
                if (!encrypted || !CustomerEnvelope(version.Ciphertext, Read(root, "encryptedDek"), Read(root, "encryptedDekNonce"), Read(root, "ephemeralPublicKey"), Read(root, "contentNonce"), versionNumber))
                    return "Version snapshot lacks its encryption envelope.";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            { return "Version snapshot metadata is invalid."; }
            using var snapshot = StrictJson.Parse(version.EnvelopeMetadata, 1024 * 1024);
            var snapshotVersion = SnapshotEnvelopeVersion(snapshot.RootElement);
            var error = CheckReference(refs, contexts, keys, "version", version.Id, "body", null, Guid.Empty, snapshotVersion, requireComplete, version.OriginalKnowledgeId);
            if (error != null) return error;
            error = CheckCanonicalKeyRef(Read(snapshot.RootElement, "encryptionKeyRef"), refs, keys, "version", version.Id, "body");
            if (error != null) return error;
            error = CheckFields(Read(snapshot.RootElement, "encryptedFieldsJson"), "version", version.Id, refs, contexts, keys, requireComplete);
            if (error != null) return error;
        }
        if (items.Any(i => package.Data.KnowledgeItems.Any(p => p.Id == i.Id))
            || comments.Any(c => package.Data.Comments.Any(p => p.Id == c.Id))
            || files.Any(f => package.Data.FileRecords.Any(p => p.Id == f.Id)))
            return "Graph duplicates encrypted records as plaintext placeholders.";
        var plainVersions = package.PlainVersions ?? [];
        if (Duplicates(plainVersions.Select(v => v.Id))) return "Duplicate plaintext version identities.";
        foreach (var version in plainVersions)
        {
            if (!package.Data.KnowledgeItems.Any(k => k.Id == version.KnowledgeId) && !items.Any(k => k.Id == version.KnowledgeId) || version.OriginalKnowledgeId == Guid.Empty
                || version.VersionNumber < 1 || version.StorageStrategy != "full")
                return "Plain history requires a known owner and materialized complete snapshots.";
            try
            {
                var fields = PlainVersionSnapshot.ReadEncryptedFields(version.SnapshotJson);
                var error = CheckFields(fields, "version", version.Id, refs, contexts, keys, requireComplete);
                if (error != null) return error;
                if (HasField(fields, "title") && !string.IsNullOrEmpty(version.Title) && version.Title != "Encrypted item"
                    || HasField(fields, "summary") && !string.IsNullOrEmpty(version.Summary)
                    || HasField(fields, "content") && !string.IsNullOrEmpty(version.Content)
                    || HasField(fields, "tags") && !string.IsNullOrEmpty(version.Tags))
                    return "Protected history fields have plaintext companions.";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KnowzArchiveException)
            { return "Plain history snapshot metadata is malformed or contains unsupported encrypted body state."; }
        }
        var parentMap = comments.Select(c => (c.Id, c.KnowledgeId, c.ParentCommentId))
            .Concat(package.Data.Comments.Select(c => (c.Id, c.KnowledgeId, c.ParentCommentId))).ToDictionary(c => c.Id);
        foreach (var comment in parentMap.Values)
        {
            if (!itemIds.Contains(comment.KnowledgeId)) return "Comment references a missing knowledge owner.";
            var visited = new HashSet<Guid> { comment.Id }; var current = comment;
            while (current.ParentCommentId is { } parent)
            {
                if (!visited.Add(parent)) return "Comment parent cycle is invalid.";
                if (!parentMap.TryGetValue(parent, out var parentComment)) return "Comment references a missing parent.";
                if (parentComment.KnowledgeId != comment.KnowledgeId) return "Comment parent belongs to another knowledge item.";
                current = parentComment;
            }
        }
        var expected = new HashSet<(string Kind, Guid Id, string Role, string? Field)>();
        foreach (var item in items) { expected.Add(("knowledge", item.Id, "body", null)); AddFields(expected, "knowledge", item.Id, item.EncryptedFieldsJson); }
        foreach (var item in data.KnowledgeItems) AddFields(expected, "knowledge", item.Id, item.EncryptedFieldsJson);
        foreach (var item in dependencyItems) AddFields(expected, "knowledge", item.Id, item.EncryptedFieldsJson);
        foreach (var version in dependencyVersions) AddFields(expected, "version", version.Id, version.EncryptedFieldsJson);
        foreach (var comment in comments) { if (!string.IsNullOrEmpty(comment.Ciphertext)) expected.Add(("comment", comment.Id, "body", null)); AddFields(expected, "comment", comment.Id, comment.EncryptedFieldsJson); }
        foreach (var file in files) { expected.Add(("file", file.Id, "binary", null)); AddFields(expected, "file", file.Id, file.EncryptedFieldsJson); }
        foreach (var file in package.Data.FileRecords) AddFields(expected, "file", file.Id, file.EncryptedFieldsJson);
        foreach (var attachment in attachments) AddFields(expected, "attachment", attachment.Id, attachment.EncryptedFieldsJson);
        foreach (var version in versions)
        {
            expected.Add(("version", version.Id, "body", null));
            using var doc = StrictJson.Parse(version.EnvelopeMetadata, 1024 * 1024);
            AddFields(expected, "version", version.Id, Read(doc.RootElement, "encryptedFieldsJson"));
        }
        foreach (var version in plainVersions) AddFields(expected, "version", version.Id, PlainVersionSnapshot.ReadEncryptedFields(version.SnapshotJson));
        var actual = refs.Select(r => (r.OwnerKind, r.OwnerId, r.Role, r.FieldName)).ToList();
        if (actual.Count != expected.Count || !expected.SetEquals(actual))
            return "Encrypted graph has missing, duplicated or unowned payload references.";
        foreach (var reference in refs)
        {
            if (reference.OwnerKind is not ("knowledge" or "comment" or "file" or "attachment" or "version")
                || reference.Role is not ("body" or "field" or "binary" or "variant"))
                return "Unknown encrypted payload family or role.";
            var exists = reference.OwnerKind switch
            {
                "knowledge" => items.Any(i => i.Id == reference.OwnerId) || data.KnowledgeItems.Any(i => i.Id == reference.OwnerId) || dependencyItems.Any(i => i.Id == reference.OwnerId),
                "comment" => comments.Any(i => i.Id == reference.OwnerId),
                "file" => files.Any(i => i.Id == reference.OwnerId) || package.Data.FileRecords.Any(i => i.Id == reference.OwnerId),
                "attachment" => attachments.Any(i => i.Id == reference.OwnerId),
                "version" => versions.Any(i => i.Id == reference.OwnerId) || plainVersions.Any(i => i.Id == reference.OwnerId) || dependencyVersions.Any(i => i.Id == reference.OwnerId),
                _ => false,
            };
            if (!exists) return "Encrypted payload reference has no owner.";
        }
        return null;
    }

    private sealed record FileLineageRow(Guid Id, Guid? RootId, Guid? PreviousId, int Version, bool Current);
    private static string? CheckFileLineage(PortableExportPackage package, bool complete)
    {
        var rows = (package.SealedFiles ?? []).Select(f => new FileLineageRow(f.Id, f.RootFileRecordId, f.PreviousFileRecordId, f.FileVersion, f.IsCurrentVersion))
            .Concat(package.Data.FileRecords.Select(f => new FileLineageRow(f.Id, f.RootFileRecordId, f.PreviousFileRecordId, f.FileVersion, f.IsCurrentVersion))).ToList();
        if (Duplicates(rows.Select(f => f.Id))) return "File lineage has empty or duplicate identities.";
        var index = rows.ToDictionary(f => f.Id);
        bool DeclaredMissing(Guid id) => !complete && (package.Omissions ?? []).Any(o => o.OwnerKind == "file" && o.OwnerId == id && !string.IsNullOrWhiteSpace(o.Reason));
        foreach (var row in rows)
        {
            var rootId = row.RootId ?? row.Id;
            if (row.Version < 1 || rootId == Guid.Empty || row.PreviousId == Guid.Empty)
                return "File lineage has an invalid version or reference.";
            if (!index.TryGetValue(rootId, out var root))
            { if (!DeclaredMissing(rootId)) return "File lineage is missing its root."; }
            else if (root.Version != 1 || (root.RootId ?? root.Id) != rootId || root.PreviousId.HasValue)
                return "File lineage root is invalid.";
            if (row.Version == 1 && (rootId != row.Id || row.PreviousId.HasValue)
                || row.Version > 1 && !row.PreviousId.HasValue)
                return "File lineage version lacks its exact ancestry.";
            if (row.PreviousId is { } previousId)
            {
                if (!index.TryGetValue(previousId, out var previous))
                { if (!DeclaredMissing(previousId)) return "File lineage is missing its previous version."; }
                else if ((previous.RootId ?? previous.Id) != rootId || previous.Version != row.Version - 1)
                    return "File lineage has a cycle, skipped version or foreign root.";
            }
        }
        foreach (var group in rows.GroupBy(f => f.RootId ?? f.Id))
            if (group.Count(f => f.Current) > 1 || group.GroupBy(f => f.Version).Any(g => g.Count() > 1))
                return "File lineage has duplicate current or historical versions.";
        return null;
    }

    private static string? CheckRelationshipClosure(PortableExportPackage package, bool allowDetachedReferences)
    {
        var data = package.Data;
        var vaults = data.Vaults.Select(v => v.Id).ToHashSet();
        var topics = data.Topics.Select(v => v.Id).ToHashSet();
        var tags = data.Tags.Select(v => v.Id).ToHashSet();
        var persons = data.Persons.Select(v => v.Id).ToHashSet();
        var locations = data.Locations.Select(v => v.Id).ToHashSet();
        var events = data.Events.Select(v => v.Id).ToHashSet();
        bool InvalidIds(List<Guid>? ids, HashSet<Guid> catalog) => ids == null
            || ids.Any(id => id == Guid.Empty || !allowDetachedReferences && !catalog.Contains(id));
        bool InvalidLinks(List<PortableEntityLink>? links, HashSet<Guid> catalog) => links != null
            && links.Any(link => link == null || link.EntityId == Guid.Empty
                || !allowDetachedReferences && !catalog.Contains(link.EntityId));
        foreach (var vault in data.Vaults)
            if (InvalidIds(vault.PersonIds, persons)) return "Vault subject list is missing or references an absent person.";
        var owners = data.KnowledgeItems.Select(i => (i.VaultIds, i.PrimaryVaultId, i.TopicId, i.TagIds,
                i.PersonIds, i.LocationIds, i.EventIds, i.PersonLinks, i.LocationLinks, i.EventLinks))
            .Concat((package.SealedItems ?? []).Select(i => (i.VaultIds, i.PrimaryVaultId, i.TopicId, i.TagIds,
                i.PersonIds, i.LocationIds, i.EventIds, i.PersonLinks, i.LocationLinks, i.EventLinks)));
        foreach (var owner in owners)
        {
            if (InvalidIds(owner.VaultIds, vaults)
                || owner.PrimaryVaultId.HasValue && !owner.VaultIds.Contains(owner.PrimaryVaultId.Value))
                return "Knowledge owning vault list is missing or its primary vault is not an included member.";
            if (owner.TopicId.HasValue && (owner.TopicId == Guid.Empty || !allowDetachedReferences && !topics.Contains(owner.TopicId.Value))
                || InvalidIds(owner.TagIds, tags) || InvalidIds(owner.PersonIds, persons)
                || InvalidIds(owner.LocationIds, locations) || InvalidIds(owner.EventIds, events)
                || InvalidLinks(owner.PersonLinks, persons) || InvalidLinks(owner.LocationLinks, locations)
                || InvalidLinks(owner.EventLinks, events))
                return "Knowledge relationship list is missing or references an absent catalog record.";
        }
        return null;
    }

    private static string? CheckReference(List<PortablePayloadKeyReference> refs, List<ExportedCryptoContext> contexts,
        List<ExportedCryptoKeyIdentity> keys, string ownerKind, Guid ownerId, string role, string? fieldName,
        Guid binding, int? version, bool requireComplete, Guid? originalSubject = null)
    {
        var matches = refs.Where(r => r.OwnerKind == ownerKind && r.OwnerId == ownerId && r.Role == role && r.FieldName == fieldName).ToList();
        if (matches.Count != 1) return "Encrypted payload requires one exact key reference.";
        var reference = matches[0];
        if (reference.AlgorithmProfile is not ("x25519-vault" or "x25519-item")
            || reference.KeyType is not ("x25519-vault-public" or "x25519-item-public")
            || reference.KeyType == "x25519-vault-public" && reference.AlgorithmProfile != "x25519-vault"
            || reference.KeyType == "x25519-item-public" && (reference.AlgorithmProfile != "x25519-item"
                || reference.EnvelopeVersion != 2 || reference.OriginalItemId is null || reference.OriginalItemId == Guid.Empty))
            return "Encrypted payload algorithm or key type is unsupported.";
        var context = contexts.SingleOrDefault(c => c.ContextId == reference.ContextId);
        if (context == null || reference.OriginalBindingId != context.OriginalBindingId
            || reference.AlgorithmProfile != context.AlgorithmProfile
            || reference.OriginalBindingKind != context.BindingKind || binding != Guid.Empty && binding != context.OriginalBindingId
            || version.HasValue && version != reference.EnvelopeVersion || reference.EnvelopeVersion is not (1 or 2))
            return "Encrypted payload binding or envelope version does not match its context.";
        // Item-bound knowledge/history ciphertext carries an immutable AAD subject. A release-lock
        // note explicitly binds to the lock, while ordinary item bodies bind to their original owner.
        // File/comment item bindings remain explicit descriptors; their parent is not an AAD inference.
        if (originalSubject.HasValue && reference.KeyType == "x25519-item-public"
            && reference.OriginalItemId != (context.BindingKind == "release-lock" ? context.OriginalBindingId : originalSubject.Value))
            return "Item-bound payload original subject does not match its immutable owner.";
        if (reference.LegacyUnattributed)
            return requireComplete ? "Legacy encrypted field key attribution is unresolved." : null;
        if (!reference.Generation.HasValue && keys.Where(k => k.ContextId == reference.ContextId && k.Fingerprint == reference.Fingerprint).Select(k => k.Generation).Distinct().Count() > 1)
            return "Encrypted payload generation is ambiguous.";
        return keys.Any(k => k.ContextId == reference.ContextId && k.Fingerprint == reference.Fingerprint && (!reference.Generation.HasValue || k.Generation == reference.Generation))
            ? null : "Encrypted payload exact historical public key is missing.";
    }

    private static string? CheckFields(string? json, string kind, Guid id, List<PortablePayloadKeyReference> refs,
        List<ExportedCryptoContext> contexts, List<ExportedCryptoKeyIdentity> keys, bool requireComplete)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = StrictJson.Parse(json, 8 * 1024 * 1024);
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetString() != "knowz-encrypted-fields-v1"
                || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                return "Unknown encrypted field-set schema.";
            foreach (var field in fields.EnumerateObject())
            {
                var envelope = field.Value;
                if (!CustomerEnvelope(Read(envelope, "ciphertext"), Read(envelope, "encryptedDek"), Read(envelope, "dekNonce"),
                        Read(envelope, "ephemeralPublicKey"), Read(envelope, "contentNonce"), 1))
                    return "Encrypted field envelope is invalid.";
                var error = CheckReference(refs, contexts, keys, kind, id, "field", field.Name, Guid.Empty, 1, requireComplete);
                if (error != null) return error;
                var intrinsicFingerprint = Read(envelope, "keyFingerprint");
                if (intrinsicFingerprint != null && !refs.Any(r => r.OwnerKind == kind && r.OwnerId == id && r.Role == "field" && r.FieldName == field.Name && r.Fingerprint == intrinsicFingerprint))
                    return "Field key fingerprint disagrees with the payload key reference.";
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { return "Encrypted field set is invalid."; }
    }

    private static void AddFields(HashSet<(string Kind, Guid Id, string Role, string? Field)> values, string kind, Guid id, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var doc = StrictJson.Parse(json, 8 * 1024 * 1024);
        foreach (var field in doc.RootElement.GetProperty("fields").EnumerateObject()) values.Add((kind, id, "field", field.Name));
    }
    private static string? CheckCanonicalKeyRef(string? value, List<PortablePayloadKeyReference> refs, List<ExportedCryptoKeyIdentity> keys, string kind, Guid id, string role)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        // Old references can lack a key generation/fingerprint; their explicit payload descriptor
        // remains authoritative. A complete canonical reference may never contradict that descriptor.
        if (parts.Length != 6 || parts[0] != "vault" || parts[4] != "sha256") return null;
        if (!Guid.TryParse(parts[1], out var binding) || !parts[3].StartsWith('v')
            || !int.TryParse(parts[3].AsSpan(1), out var generation) || generation < 1 || !IsSha256(parts[5]))
            return "Canonical encryption key reference is malformed.";
        var reference = refs.Single(r => r.OwnerKind == kind && r.OwnerId == id && r.Role == role && r.FieldName == null);
        var matchingKeys = keys.Where(k => k.ContextId == reference.ContextId && k.Fingerprint == reference.Fingerprint).ToList();
        var inferredGeneration = reference.Generation ?? (matchingKeys.Count == 1 ? matchingKeys[0].Generation : (int?)null);
        if (string.IsNullOrWhiteSpace(parts[2]) || reference.OriginalBindingId != binding || reference.Fingerprint != parts[5]
            || inferredGeneration.HasValue && inferredGeneration != generation)
            return "Canonical envelope key reference disagrees with its payload identity.";
        return null;
    }

    private static bool HasNull<T>(IEnumerable<T>? values) where T : class => values?.Any(v => v is null) == true;

    private static int SnapshotEnvelopeVersion(JsonElement root)
    {
        int? ReadVersion(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version) || version is not (1 or 2))
                throw new JsonException("Invalid historical envelope version.");
            return version;
        }
        var canonical = ReadVersion("encryptionEnvelopeVersion"); var alias = ReadVersion("envelopeVersion");
        if (canonical.HasValue && alias.HasValue && canonical != alias) throw new JsonException("Conflicting historical envelope versions.");
        return canonical ?? alias ?? 1;
    }

    private static string? Read(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool HasField(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try { using var d = StrictJson.Parse(json, 8 * 1024 * 1024); return d.RootElement.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out _); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
    public static bool IsSafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 240
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0')
        && path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Duplicates(IEnumerable<Guid> values) { var a = values.ToList(); return a.Any(v => v == Guid.Empty) || a.Count != a.Distinct().Count(); }
    private static bool CustomerEnvelope(string? ciphertext, string? dek, string? nonce, string? ephemeral, string? contentNonce, int? version)
        => TryBase64(ciphertext, 16, MaxFileBytes, out _) && EnvelopeParts(dek, nonce, ephemeral, contentNonce, version);
    private static bool EnvelopeParts(string? dek, string? nonce, string? ephemeral, string? contentNonce, int? version)
        => version is null or 1 or 2 && TryBase64(dek, 48, 48, out _) && TryBase64(nonce, 12, 12, out _)
            && TryBase64(ephemeral, 32, 32, out _) && TryBase64(contentNonce, 12, 12, out _);
    private static bool TryBase64(string? value, long min, long max, out byte[] bytes)
    {
        bytes = [];
        if (value == null || value.Length > ((max + 2) / 3) * 4 || value.Length < ((min + 2) / 3) * 4) return false;
        try { bytes = Convert.FromBase64String(value); return bytes.LongLength >= min && bytes.LongLength <= max && Convert.ToBase64String(bytes) == value; }
        catch (FormatException) { return false; }
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ExportedVaultCryptoContextLink
{
    public Guid VaultId { get; set; }
    public Guid ContextId { get; set; }
    public bool IsPrimaryWriteContext { get; set; }
    public string? PrimaryWriteFingerprint { get; set; }
    public int? PrimaryWriteGeneration { get; set; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class PortablePayloadKeyReference
{
    public string OwnerKind { get; set; } = "knowledge";
    public Guid OwnerId { get; set; }
    public string Role { get; set; } = "body";
    public string? FieldName { get; set; }
    public Guid? VersionId { get; set; }
    public Guid ContextId { get; set; }
    public string? Fingerprint { get; set; }
    public bool LegacyUnattributed { get; set; }
    public string OriginalBindingKind { get; set; } = "vault";
    public Guid OriginalBindingId { get; set; }
    public Guid? OriginalItemId { get; set; }
    public string KeyType { get; set; } = "x25519-vault-public";
    public string AlgorithmProfile { get; set; } = "x25519-vault";
    public int EnvelopeVersion { get; set; } = 1;
    public int? Generation { get; set; }
}

public sealed class SealedAttachmentRecord
{
    public Guid Id { get; set; }
    public Guid OriginalAttachmentId { get; set; }
    public Guid FileRecordId { get; set; }
    public Guid? KnowledgeId { get; set; }
    public Guid? CommentId { get; set; }
    public Guid? InboxId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPrimary { get; set; }
    public string? EncryptedFieldsJson { get; set; }
    public bool ExcludeFromAiProcessing { get; set; }
    public bool ExcludeFromVersionDiff { get; set; }
    public DateTime AttachedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Strict JSON parsing shared by the archive and encrypted graph.</summary>
public static class StrictJson
{
    public static JsonDocument Parse(string json, int maxBytes)
    {
        if (json.Length > maxBytes || System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes) throw new JsonException("JSON exceeds its size limit.");
        return Parse(System.Text.Encoding.UTF8.GetBytes(json), maxBytes);
    }
    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int maxBytes)
    {
        if (bytes.Length > maxBytes) throw new JsonException("JSON exceeds its size limit.");
        var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { ValidateObject(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in value.EnumerateObject())
            {
                if (!names.Add(prop.Name) || prop.Name is "__proto__" or "constructor" or "prototype")
                    throw new JsonException("Duplicate or forbidden JSON property.");
                ValidateObject(prop.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) ValidateObject(child);
    }
}
