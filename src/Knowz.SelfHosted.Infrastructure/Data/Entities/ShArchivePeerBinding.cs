namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>Public, reviewed identity of an inbound or linked archive peer; never holds a private key or credential.</summary>
public sealed class ShArchivePeerBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid TenantId { get; set; }
    public Guid DestinationVaultId { get; set; }
    public Guid SourceTenantId { get; set; }
    public Guid SourceVaultId { get; set; }
    /// <summary>Descriptive exact URL from reviewed configuration; never a server-side fetch target.</summary>
    public string SourceBaseUrl { get; set; } = "";
    /// <summary>SHA256 of the canonical tenant/vault/URL slot. Excludes rotating credentials and signer.</summary>
    public string BindingSlotSha256 { get; set; } = "";
    public string SignerFingerprint { get; set; } = "";
    public string SignerPublicKey { get; set; } = "";
    public string CredentialIdentitySha256 { get; set; } = "";
    public Guid? PrincipalUserId { get; set; }
    // Logical anchors can live in another database; authorization re-resolves them.
    public string? ParentBindingSha256 { get; set; }
    public string? RegistrationChallengeSha256 { get; set; }
    public string? PendingRemoteChallengeJson { get; set; }
    public string? PendingLocalSourceBaseUrl { get; set; }
    public Guid? RemoteBindingId { get; set; }
    public string? RemoteBindingRevision { get; set; }
    public string? LocalSourceBaseUrl { get; set; }
    public Guid? PlatformConnectionId { get; set; }
    public Guid? SyncLinkId { get; set; }
    public string TrustProvenance { get; set; } = "transport-authenticated-tofu";
    public bool IsEnabled { get; set; } = true;
    public Guid? RegisteredByUserId { get; set; }
    /// <summary>Replaced only by explicit reviewed registration; changing it invalidates all earlier plans.</summary>
    public string BindingRevision { get; set; } = Guid.NewGuid().ToString("N");
}
