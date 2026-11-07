using System.Text;

namespace Knowz.Core.Portability;

/// <summary>
/// Classifies an offline payload snapshot. This type has no repair operation.
/// </summary>
public static class CensusKinds
{
    public const string BodyPlaintext = "body-plaintext";
    public const string BodyEncrypted = "body-encrypted";
    public const string EncryptedFields = "encrypted-fields";
    public const string Comment = "comment";
    public const string Attachment = "attachment";
    public const string FileRecordBlob = "file-record-blob";
    public const string FileRecordVariant = "file-record-variant";
    public const string Version = "version";
    public const string VersionUnrecoverable = "version-unrecoverable";
    public const string KeyConfig = "key-config";
    public const string KeyHistory = "key-history";
    public const string InheritedVault = "inherited-vault";
    public const string SharedItem = "shared-item";
    public const string LockReleased = "lock-released";
    public const string LockUnreleased = "lock-unreleased";
    public const string PendingProcessing = "pending-processing";

    public static readonly string[] All =
    {
        BodyPlaintext,
        BodyEncrypted,
        EncryptedFields,
        Comment,
        Attachment,
        FileRecordBlob,
        FileRecordVariant,
        Version,
        VersionUnrecoverable,
        KeyConfig,
        KeyHistory,
        InheritedVault,
        SharedItem,
        LockReleased,
        LockUnreleased,
        PendingProcessing,
    };
}

public sealed class CensusKnowledgeRow
{
    public Guid Id { get; init; }
    public bool HasPlaintextBody { get; init; }
    public bool HasEncryptedBody { get; init; }
    public bool HasEncryptedFields { get; init; }
}

public sealed class CensusCommentRow
{
    public Guid Id { get; init; }
    public bool HasPlaintextBody { get; init; }
    public bool HasEncryptedBody { get; init; }
}

public sealed class CensusAttachmentRow
{
    public Guid Id { get; init; }
    public bool OnComment { get; init; }
}

public sealed class CensusFileRow
{
    public Guid Id { get; init; }
    public bool HasBlob { get; init; }
    public bool HasVariant { get; init; }
    public bool IsEncrypted { get; init; }
}

public sealed class CensusVersionRow
{
    public Guid Id { get; init; }
    public bool HasRecoverableEnvelope { get; init; }
}

public sealed class CensusKeyRow
{
    public Guid Id { get; init; }
    public int Generation { get; init; }
    public bool IsCurrentConfig { get; init; }
    public bool IsHistorical { get; init; }
}

public sealed class CensusVaultRow
{
    public Guid Id { get; init; }
    public Guid? ParentVaultId { get; init; }
}

public sealed class CensusShareRow
{
    public Guid ItemId { get; init; }
}

public sealed class CensusLockRow
{
    public Guid Id { get; init; }
    public bool Released { get; init; }
}

public sealed class CensusPendingRow
{
    public Guid Id { get; init; }
    public string State { get; init; } = "pending";
}

public sealed class CensusInput
{
    public List<CensusKnowledgeRow> Knowledge { get; init; } = new();
    public List<CensusCommentRow> Comments { get; init; } = new();
    public List<CensusAttachmentRow> Attachments { get; init; } = new();
    public List<CensusFileRow> Files { get; init; } = new();
    public List<CensusVersionRow> Versions { get; init; } = new();
    public List<CensusKeyRow> Keys { get; init; } = new();
    public List<CensusVaultRow> Vaults { get; init; } = new();
    public List<CensusShareRow> Shares { get; init; } = new();
    public List<CensusLockRow> Locks { get; init; } = new();
    public List<CensusPendingRow> Pending { get; init; } = new();
}

/// <summary>
/// Locks and file variants that a portable package cannot express on its own.
/// </summary>
public sealed class CensusAnnotations
{
    public List<CensusLockRow> Locks { get; init; } = new();
    public HashSet<Guid> VariantFileIds { get; init; } = new();
}

public sealed class CensusRow
{
    public string Kind { get; init; } = string.Empty;
    public string OwnerKind { get; init; } = string.Empty;
    public Guid OwnerId { get; init; }
    public string Disposition { get; init; } = "classified";
}

public sealed class CensusReport
{
    public bool RepairPerformed { get; init; }
    public string Statement { get; init; } = "Classification only. No repair was performed.";
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<CensusRow> Rows { get; init; } = Array.Empty<CensusRow>();

    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("encrypted-payload-census");
        builder.Append("repairPerformed: ").AppendLine(RepairPerformed ? "true" : "false");
        builder.Append("statement: ").AppendLine(Statement);
        foreach (var pair in Counts.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append(": ").AppendLine(pair.Value.ToString());
        return builder.ToString();
    }
}

public static class EncryptedPayloadCensus
{
    public static CensusReport Classify(CensusInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rows = new List<CensusRow>();

        void Add(string kind, string owner, Guid id) =>
            rows.Add(new CensusRow { Kind = kind, OwnerKind = owner, OwnerId = id, Disposition = "classified" });

        foreach (var knowledge in input.Knowledge)
        {
            if (knowledge.HasPlaintextBody)
                Add(CensusKinds.BodyPlaintext, "knowledge", knowledge.Id);
            if (knowledge.HasEncryptedBody)
                Add(CensusKinds.BodyEncrypted, "knowledge", knowledge.Id);
            if (knowledge.HasEncryptedFields)
                Add(CensusKinds.EncryptedFields, "knowledge", knowledge.Id);
        }

        foreach (var comment in input.Comments)
        {
            Add(CensusKinds.Comment, "comment", comment.Id);
            if (comment.HasPlaintextBody)
                Add(CensusKinds.BodyPlaintext, "comment", comment.Id);
            if (comment.HasEncryptedBody)
                Add(CensusKinds.BodyEncrypted, "comment", comment.Id);
        }

        foreach (var attachment in input.Attachments)
            Add(CensusKinds.Attachment, attachment.OnComment ? "comment-attachment" : "attachment", attachment.Id);

        foreach (var file in input.Files)
        {
            if (file.HasBlob)
                Add(CensusKinds.FileRecordBlob, "file", file.Id);
            if (file.HasVariant)
                Add(CensusKinds.FileRecordVariant, "file", file.Id);
            if (file.IsEncrypted)
                Add(CensusKinds.BodyEncrypted, "file", file.Id);
        }

        foreach (var version in input.Versions)
        {
            Add(CensusKinds.Version, "version", version.Id);
            if (!version.HasRecoverableEnvelope)
                Add(CensusKinds.VersionUnrecoverable, "version", version.Id);
        }

        foreach (var key in input.Keys)
        {
            if (key.IsCurrentConfig)
                Add(CensusKinds.KeyConfig, "key", key.Id);
            if (key.IsHistorical || key.Generation > 1)
                Add(CensusKinds.KeyHistory, "key", key.Id);
        }

        foreach (var vault in input.Vaults)
        {
            if (vault.ParentVaultId.HasValue)
                Add(CensusKinds.InheritedVault, "vault", vault.Id);
        }

        foreach (var share in input.Shares)
            Add(CensusKinds.SharedItem, "share", share.ItemId);

        foreach (var lockRow in input.Locks)
            Add(lockRow.Released ? CensusKinds.LockReleased : CensusKinds.LockUnreleased, "lock", lockRow.Id);

        foreach (var pending in input.Pending)
            Add(CensusKinds.PendingProcessing, "pending", pending.Id);

        var counts = rows.GroupBy(r => r.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var kind in CensusKinds.All)
            counts.TryAdd(kind, 0);

        return new CensusReport
        {
            RepairPerformed = false,
            Statement = "Classification only. No repair was performed.",
            Counts = counts,
            Rows = rows,
        };
    }

    public static CensusReport ClassifyPackage(PortableExportPackage package, CensusAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        annotations ??= new CensusAnnotations();
        var sealedItems = package.SealedItems ?? new List<SealedKnowledgeItem>();
        var knowledge = new List<CensusKnowledgeRow>();
        var seen = new HashSet<Guid>();

        foreach (var item in package.Data?.KnowledgeItems ?? new List<PortableKnowledge>())
        {
            seen.Add(item.Id);
            var sealedItem = sealedItems.FirstOrDefault(s => s.Id == item.Id || s.OriginalItemId == item.Id);
            knowledge.Add(new CensusKnowledgeRow
            {
                Id = item.Id,
                HasPlaintextBody = !string.IsNullOrEmpty(item.Content),
                HasEncryptedBody = sealedItem != null && !string.IsNullOrWhiteSpace(sealedItem.Ciphertext),
                HasEncryptedFields = !string.IsNullOrWhiteSpace(item.EncryptedFieldsJson) || !string.IsNullOrWhiteSpace(sealedItem?.EncryptedFieldsJson),
            });
        }

        foreach (var sealedItem in sealedItems)
        {
            if (!seen.Add(sealedItem.Id))
                continue;
            knowledge.Add(new CensusKnowledgeRow
            {
                Id = sealedItem.Id,
                HasEncryptedBody = !string.IsNullOrWhiteSpace(sealedItem.Ciphertext),
                HasEncryptedFields = !string.IsNullOrWhiteSpace(sealedItem.EncryptedFieldsJson),
            });
        }

        var comments = (package.Data?.Comments ?? new List<PortableKnowledgeComment>())
            .Select(c => new CensusCommentRow
            {
                Id = c.Id,
                HasPlaintextBody = !string.IsNullOrEmpty(c.Body),
            })
            .ToList();

        var attachments = new List<CensusAttachmentRow>();
        var files = new List<CensusFileRow>();
        foreach (var file in package.Data?.FileRecords ?? new List<PortableFileRecord>())
        {
            var hasBlob = !string.IsNullOrWhiteSpace(file.BlobUri)
                || !string.IsNullOrWhiteSpace(file.BinaryContentBase64)
                || !string.IsNullOrWhiteSpace(file.BinaryFilePath);
            files.Add(new CensusFileRow
            {
                Id = file.Id,
                HasBlob = hasBlob,
                HasVariant = annotations.VariantFileIds.Contains(file.Id),
            });
            foreach (var link in file.Attachments ?? new List<PortableFileAttachmentLink>())
            {
                attachments.Add(new CensusAttachmentRow
                {
                    Id = link.KnowledgeId ?? link.CommentId ?? file.Id,
                    OnComment = link.CommentId.HasValue,
                });
            }
        }

        foreach (var sealedFile in package.SealedFiles ?? new List<SealedFileRecord>())
        {
            files.Add(new CensusFileRow
            {
                Id = sealedFile.Id,
                HasBlob = !string.IsNullOrWhiteSpace(sealedFile.BinaryContentBase64),
                IsEncrypted = !string.IsNullOrWhiteSpace(sealedFile.BinaryContentBase64),
            });
        }

        var versions = (package.SealedVersions ?? new List<SealedKnowledgeVersionRecord>())
            .Select(v => new CensusVersionRow
            {
                Id = v.Id,
                HasRecoverableEnvelope = !string.IsNullOrWhiteSpace(v.Ciphertext)
                    && !string.IsNullOrWhiteSpace(v.EnvelopeMetadata)
                    && v.EnvelopeMetadata.Contains("\"sealedSnapshot\":true", StringComparison.Ordinal),
            })
            .ToList();

        var keys = (package.CryptoKeyCatalog ?? new List<ExportedCryptoKeyIdentity>())
            .Select(k => new CensusKeyRow
            {
                Id = k.ContextId,
                Generation = k.Generation,
                IsCurrentConfig = k.Generation <= 1 && !string.Equals(k.Lifecycle, "decrypt-only", StringComparison.Ordinal),
                IsHistorical = k.Generation > 1 || string.Equals(k.Lifecycle, "decrypt-only", StringComparison.Ordinal),
            })
            .ToList();

        var vaults = (package.Data?.Vaults ?? new List<PortableVault>())
            .Select(v => new CensusVaultRow { Id = v.Id, ParentVaultId = v.ParentVaultId })
            .ToList();

        var shares = (package.Data?.KnowledgeItems ?? new List<PortableKnowledge>())
            .Where(k => k.VaultIds != null && k.VaultIds.Count > 1)
            .Select(k => new CensusShareRow { ItemId = k.Id })
            .ToList();

        var pending = (package.Omissions ?? new List<PortableOmission>())
            .Where(o => string.Equals(o.Reason, EncryptedPortabilityProfile.ReasonPending, StringComparison.Ordinal))
            .Select(o => new CensusPendingRow { Id = o.OwnerId, State = o.Reason })
            .ToList();

        return Classify(new CensusInput
        {
            Knowledge = knowledge,
            Comments = comments,
            Attachments = attachments,
            Files = files,
            Versions = versions,
            Keys = keys,
            Vaults = vaults,
            Shares = shares,
            Locks = annotations.Locks,
            Pending = pending,
        });
    }
}
