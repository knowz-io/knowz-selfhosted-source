namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>Exact committed context/plan association; unverified origin remains unverified.</summary>
public sealed class ShCryptoContextImportAnchor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid ContextId { get; set; }
    public Guid OriginalBindingId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string MappingSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string SourceTrust { get; set; } = "unverified";
    public string PublicKeyCatalogJson { get; set; } = "[]";
}

/// <summary>Public one-time possession state only; no encrypted canary, secret or private key escrow.</summary>
public sealed class ShCryptoCustodyChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public Guid VaultId { get; set; }
    public Guid ContextId { get; set; }
    public Guid KeyId { get; set; }
    public Guid PlanId { get; set; }
    public int Generation { get; set; }
    public string Fingerprint { get; set; } = "";
    public string KeyStateSha256 { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string BindingJson { get; set; } = "";
    public string ExpectedProofSha256 { get; set; } = "";
    public string Status { get; set; } = "issued";
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Local exact-key consent, separate from source custody and full archive verification.</summary>
public sealed class ShRecoveredKeyCustodyAcceptance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public Guid ContextId { get; set; }
    public Guid KeyId { get; set; }
    public Guid PlanId { get; set; }
    public int Generation { get; set; }
    public string Fingerprint { get; set; } = "";
    public string KeyStateSha256 { get; set; } = "";
    public string SourceTrust { get; set; } = "unverified";
    public DateTime AcceptedAt { get; set; }
}
