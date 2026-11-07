using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Microsoft.AspNetCore.TestHost;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false, 65, false)]
    [InlineData(true, 65, true)]
    [InlineData(false, 1, true)]
    public async Task SelfHostedSignedOrdinaryPushNegotiatesBeforeArchiveBody(bool newCapability, int mib, bool allowed)
    {
        await using var local = new PeerSide(); await using var remote = new PeerSide();
        remote.CapabilitiesResponse = new JsonObject { ["archiveSchemaVersion"] = 4, ["graphSchemaVersion"] = 5, ["limits"] = new JsonObject { ["archiveBytes"] = 32L * 1024 * 1024 * 1024 } };
        if (newCapability) remote.CapabilitiesResponse["limits"]!["ordinaryFileBytes"] = 32L * 1024 * 1024 * 1024;
        var bytes = RandomNumberGenerator.GetBytes(mib * 1024 * 1024); var fileId = Guid.NewGuid(); var noteId = Guid.NewGuid();
        var package = new PortableExportPackage { SchemaVersion = 5, RequiredCapabilities = [EncryptedGraphProfile.Capability], SourceTenantId = local.Tenant.TenantId,
            Scope = new() { Type = ExportScopeType.Vault, VaultIds = [local.Vault.Id] }, Data = new() {
                Vaults = [new() { Id = local.Vault.Id, Name = "Ordinary peer vault" }],
                KnowledgeItems = [new() { Id = noteId, Title = "Ordinary peer source", Content = "source", PrimaryVaultId = local.Vault.Id, VaultIds = [local.Vault.Id] }],
                FileRecords = [new() { Id = fileId, OriginalFileId = fileId, FileName = "ordinary.bin", ContentType = "application/octet-stream", SizeBytes = bytes.Length, BinaryContentBase64 = Convert.ToBase64String(bytes), Attachments = [new() { KnowledgeId = noteId }] }] } };
        var imported = await local.Graph.ImportAsync(package, local.Vault.Id, "trusted:fixture", ImportConflictStrategy.Skip, null, default);
        Assert.True(imported.Success, imported.Error);
        await remote.StartAsync(); var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection); var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true }; local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => remote.App!.GetTestClient());
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator,
            new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance));
        await transport.RegisterAsync(local.User.Id, link.Id, "https://local.example", null, default);
        if (allowed) { var receipt = await transport.TransferAsync(local.User.Id, link.Id, "push", default); Assert.Equal("complete", receipt.PreservationState); Assert.Equal(1, remote.ArchivePlanBodyPosts); }
        else { var failure = await Assert.ThrowsAsync<ArchiveOperationException>(() => transport.TransferAsync(local.User.Id, link.Id, "push", default)); Assert.Equal(409, failure.StatusCode); Assert.Equal(0, remote.ArchivePlanBodyPosts); Assert.Empty(remote.Db.FileRecords); }
    }
}
