using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Infrastructure.Data;

public partial class SelfHostedDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { GuardInactiveWritesAsync(false, default).GetAwaiter().GetResult(); StampPeerSecurityRevision(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { await GuardInactiveWritesAsync(true, cancellationToken); StampPeerSecurityRevision(); return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    private void StampPeerSecurityRevision()
    {
        ChangeTracker.DetectChanges();
        string[] connectionFields = [nameof(PlatformConnection.TenantId), nameof(PlatformConnection.PlatformApiUrl), nameof(PlatformConnection.ApiKeyProtected), nameof(PlatformConnection.RemoteTenantId)];
        foreach (var entry in ChangeTracker.Entries<PlatformConnection>().Where(e => e.State == EntityState.Modified))
            if (connectionFields.Any(name => entry.Property(name).IsModified)) entry.Entity.SecurityRevision = Guid.NewGuid().ToString("N");
#pragma warning disable CS0618 // Legacy credentials are still security configuration during their migration window.
        string[] linkFields = [nameof(VaultSyncLink.LocalVaultId), nameof(VaultSyncLink.RemoteVaultId), nameof(VaultSyncLink.RemoteTenantId),
            nameof(VaultSyncLink.PlatformConnectionId), nameof(VaultSyncLink.PlatformApiUrl), nameof(VaultSyncLink.ApiKeyEncrypted), nameof(VaultSyncLink.SyncEnabled)];
#pragma warning restore CS0618
        foreach (var entry in ChangeTracker.Entries<VaultSyncLink>().Where(e => e.State == EntityState.Modified))
            if (linkFields.Any(name => entry.Property(name).IsModified)) entry.Entity.SecurityRevision = Guid.NewGuid().ToString("N");
    }
}
