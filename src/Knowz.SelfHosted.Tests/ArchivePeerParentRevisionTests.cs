using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class ArchivePeerParentRevisionTests
{
    [Fact]
    public async Task ConnectionConfigurationChangesAndReversionFencePeersButHealthChecksDoNot()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var connection = new PlatformConnection { TenantId = tenant.TenantId, PlatformApiUrl = "https://source.example", ApiKeyProtected = "synthetic-protected-credential" };
        db.PlatformConnections.Add(connection); await db.SaveChangesAsync(); var original = connection.SecurityRevision;
        connection.LastTestedAt = DateTime.UtcNow; connection.LastTestStatus = PlatformConnectionTestStatus.Ok; connection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); Assert.Equal(original, connection.SecurityRevision);
        connection.ApiKeyProtected = "replacement-protected-credential"; await db.SaveChangesAsync(); var changed = connection.SecurityRevision; Assert.NotEqual(original, changed);
        connection.ApiKeyProtected = "synthetic-protected-credential"; db.SaveChanges(); Assert.NotEqual(original, connection.SecurityRevision); Assert.NotEqual(changed, connection.SecurityRevision);
        Assert.NotNull(db.Model.FindEntityType(typeof(ShArchivePeerBinding))!.FindProperty("ParentBindingSha256"));
    }
    [Fact]
    public async Task LinkTargetAndEnabledChangesFencePeersButSuccessfulSyncDoesNot()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(Guid.NewGuid());
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var link = new VaultSyncLink { LocalVaultId = Guid.NewGuid(), RemoteVaultId = Guid.NewGuid(), RemoteTenantId = Guid.NewGuid() };
        db.VaultSyncLinks.Add(link); await db.SaveChangesAsync(); var original = link.SecurityRevision;
        link.LastSyncCompletedAt = DateTime.UtcNow; link.LastSyncStatus = VaultSyncStatus.Succeeded; link.LastPullCursor = DateTime.UtcNow; link.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); Assert.Equal(original, link.SecurityRevision);
        link.SyncEnabled = false; await db.SaveChangesAsync(); Assert.NotEqual(original, link.SecurityRevision); var disabled = link.SecurityRevision;
        link.SyncEnabled = true; await db.SaveChangesAsync(); Assert.NotEqual(original, link.SecurityRevision); Assert.NotEqual(disabled, link.SecurityRevision);
    }
}
