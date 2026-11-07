using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData("not-registered")]
    [InlineData("registered")]
    [InlineData("pending")]
    [InlineData("review-required")]
    [InlineData("revoked")]
    [InlineData("foreign-link")]
    public async Task PublicSavedPeerReviewReturnsOnlyCurrentAuthorizedPublicBinding(string state)
    {
        await using var local = new PeerSide(); await using var remote = new PeerSide(); await remote.StartAsync();
        var connection = new PlatformConnection { TenantId = local.Tenant.TenantId, PlatformApiUrl = "https://remote.example", RemoteTenantId = remote.Tenant.TenantId,
            ApiKeyProtected = local.Protection.CreateProtector(PlatformConnectionService.MasterPurpose).CreateProtector($"{PlatformConnectionService.MasterPurpose}.{local.Tenant.TenantId}").Protect(remote.User.ApiKey!) };
        local.Db.PlatformConnections.Add(connection);
        var link = new VaultSyncLink { LocalVaultId = local.Vault.Id, RemoteTenantId = remote.Tenant.TenantId, RemoteVaultId = remote.Vault.Id, PlatformConnectionId = connection.Id, SyncEnabled = true };
        local.Db.VaultSyncLinks.Add(link); await local.Db.SaveChangesAsync();
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient(PlatformSyncCredentialResolver.HttpClientName).Returns(_ => remote.App!.GetTestClient());
        var validator = Substitute.For<IUrlValidator>(); validator.ValidatePlatformUrl("https://remote.example").Returns(new UrlValidationResult(true, null));
        var resolver = new PlatformSyncCredentialResolver(factory, local.Protection, validator, local.Db, NullLogger<PlatformSyncCredentialResolver>.Instance);
        var transport = new SelfHostedArchivePeerTransport(local.Db, local.Tenant, local.Peers, local.Exporter, local.Adapter, local.Coordinator, resolver);
        await local.StartAsync(transport); using var client = local.App!.GetTestClient(); client.DefaultRequestHeaders.Add("X-Api-Key", local.User.ApiKey);
        ShArchivePeerBinding? binding = null;
        if (state != "not-registered")
        {
            using var registered = await client.PostAsJsonAsync($"/api/v1/sync/links/{link.Id}/archive-peer/register", new { localSourceBaseUrl = "https://local.example" }); registered.EnsureSuccessStatusCode();
            binding = (await registered.Content.ReadFromJsonAsync<ShArchivePeerBinding>())!;
        }
        if (state == "pending")
        {
            var row = await local.Db.ShArchivePeerBindings.SingleAsync(); row.RemoteBindingId = null; row.RemoteBindingRevision = null; row.LocalSourceBaseUrl = null;
            row.PendingRemoteChallengeJson = "synthetic-private-pending-proof"; row.PendingLocalSourceBaseUrl = "https://private-pending.example"; await local.Db.SaveChangesAsync();
        }
        if (state == "review-required") { connection.PlatformApiUrl = "https://changed-peer.example"; await local.Db.SaveChangesAsync(); }
        if (state == "revoked") { local.User.IsActive = false; await local.Db.SaveChangesAsync(); }
        using var response = await client.GetAsync($"/api/v1/sync/links/{(state == "foreign-link" ? Guid.NewGuid() : link.Id)}/archive-peer");
        if (state is "revoked" or "foreign-link") { Assert.Equal(state == "revoked" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, response.StatusCode); return; }
        response.EnsureSuccessStatusCode(); var text = await response.Content.ReadAsStringAsync(); var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(state, body.GetProperty("status").GetString());
        if (binding == null) Assert.Equal(JsonValueKind.Null, body.GetProperty("binding").ValueKind);
        else { var value = body.GetProperty("binding"); Assert.Equal(binding.Id, value.GetProperty("id").GetGuid()); Assert.Equal(binding.BindingRevision, value.GetProperty("bindingRevision").GetString());
            Assert.False(value.TryGetProperty("pendingRemoteChallengeJson", out _)); Assert.False(value.TryGetProperty("pendingLocalSourceBaseUrl", out _)); }
        Assert.DoesNotContain("synthetic-private-pending-proof", text); Assert.DoesNotContain("private-pending.example", text); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }
}
