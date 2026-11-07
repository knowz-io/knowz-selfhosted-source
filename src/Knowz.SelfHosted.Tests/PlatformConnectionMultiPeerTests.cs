using System.Net;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// ADR_G6 (#1043): <see cref="PlatformConnection"/> is one-of-many per tenant. Covers the
/// additive list / by-id / create / update / test / delete / resolve members and the
/// "legacy singleton routes address the primary (earliest-created) connection" rule.
/// </summary>
public class PlatformConnectionMultiPeerTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string CloudKey = "ukz_CLOUDCLOUDCLOUDCLOUDcl0d";
    private const string PeerKey = "ksh_PEERPEERPEERPEERPEERpe3r";
    private const string CloudUrl = "https://api.knowz.io";
    private const string PeerUrl = "https://peer.example.com";

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IDataProtectionProvider _dpp;
    private readonly IUrlValidator _urlValidator;

    public PlatformConnectionMultiPeerTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantA);
        _db = new SelfHostedDbContext(options, _tenantProvider);
        _dpp = new EphemeralDataProtectionProvider();
        _urlValidator = Substitute.For<IUrlValidator>();
        _urlValidator.ValidatePlatformUrl(Arg.Any<string>()).Returns(new UrlValidationResult(true, null));
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private PlatformConnectionService CreateService(IHttpClientFactory? factory = null)
    {
        factory ??= Substitute.For<IHttpClientFactory>();
        return new PlatformConnectionService(
            _db, _tenantProvider, _dpp, _urlValidator, factory,
            Substitute.For<ILogger<PlatformConnectionService>>());
    }

    private async Task<(PlatformConnectionDto Cloud, PlatformConnectionDto Peer)> SeedTwoAsync(PlatformConnectionService service)
    {
        var cloud = await service.CreateAsync(new UpsertPlatformConnectionRequest(CloudUrl, "Cloud", CloudKey), Guid.NewGuid());
        var peer = await service.CreateAsync(new UpsertPlatformConnectionRequest(PeerUrl, "Peer", PeerKey), Guid.NewGuid());
        return (cloud, peer);
    }

    [Fact]
    public async Task CreateAsync_SecondConnection_SameTenant_Succeeds_AndListReturnsBoth()
    {
        var service = CreateService();
        var (cloud, peer) = await SeedTwoAsync(service);

        Assert.NotEqual(cloud.Id, peer.Id);
        Assert.Equal(2, await _db.PlatformConnections.CountAsync(c => c.TenantId == TenantA));

        var list = await service.ListAsync();
        Assert.Equal(new[] { cloud.Id, peer.Id }, list.Select(c => c.Id).ToArray());
        Assert.Equal(PeerUrl, list[1].PlatformApiUrl);
        Assert.Equal("ukz_****cl0d", list[0].ApiKeyMask);
        Assert.Equal("ksh_****pe3r", list[1].ApiKeyMask);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNormalizedUrl_Throws()
    {
        var service = CreateService();
        await service.CreateAsync(new UpsertPlatformConnectionRequest(PeerUrl, null, PeerKey), Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new UpsertPlatformConnectionRequest(PeerUrl + "/", null, PeerKey), Guid.NewGuid()));

        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_RequiresApiKey()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new UpsertPlatformConnectionRequest(PeerUrl, null, ApiKey: null), Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateAsync_InvalidUrl_Throws_AndPersistsNothing()
    {
        _urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(false, "Host 'peer.example.com' is not in the platform allowlist.", UrlValidationErrorCode.NotAllowlisted));
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new UpsertPlatformConnectionRequest(PeerUrl, null, PeerKey), Guid.NewGuid()));
        Assert.Equal(0, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task GetByIdAsync_OtherTenant_ReturnsNull()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);

        _tenantProvider.TenantId.Returns(TenantB);
        Assert.Null(await service.GetByIdAsync(peer.Id));
        Assert.Empty(await service.ListAsync());

        _tenantProvider.TenantId.Returns(TenantA);
        Assert.NotNull(await service.GetByIdAsync(peer.Id));
    }

    [Fact]
    public async Task ResolveForOutboundCallAsync_ById_ReturnsThatConnection_NotThePrimary()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);

        var resolved = await service.ResolveForOutboundCallAsync(peer.Id);

        Assert.NotNull(resolved);
        Assert.Equal(PeerUrl, resolved!.Value.PlatformApiUrl);
        Assert.Equal(PeerKey, resolved.Value.ApiKeyPlaintext);
    }

    [Fact]
    public async Task ResolveForOutboundCallAsync_ById_OtherTenant_ReturnsNull()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);
        _tenantProvider.TenantId.Returns(TenantB);

        Assert.Null(await service.ResolveForOutboundCallAsync(peer.Id));
    }

    [Fact]
    public async Task LegacyGetAsync_ReturnsPrimary_EarliestCreated()
    {
        var service = CreateService();
        var (cloud, _) = await SeedTwoAsync(service);

        var primary = await service.GetAsync();
        var resolved = await service.ResolveForOutboundCallAsync();

        Assert.Equal(cloud.Id, primary!.Id);
        Assert.Equal(CloudUrl, resolved!.Value.PlatformApiUrl);
    }

    [Fact]
    public async Task LegacyUpsertAsync_UpdatesPrimary_NotPeer()
    {
        var service = CreateService();
        var (cloud, peer) = await SeedTwoAsync(service);

        var dto = await service.UpsertAsync(new UpsertPlatformConnectionRequest(CloudUrl, "Renamed", null), Guid.NewGuid());

        Assert.Equal(cloud.Id, dto.Id);
        Assert.Equal("Renamed", dto.DisplayName);
        var peerRow = await _db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == peer.Id);
        Assert.Equal("Peer", peerRow.DisplayName);
        Assert.Equal(2, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_ById_ChangesUrlAndName_KeepsKey_WhenApiKeyNull()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);
        var before = await _db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == peer.Id);

        var updated = await service.UpdateAsync(peer.Id,
            new UpsertPlatformConnectionRequest("https://peer2.example.com/", "Peer 2", null), Guid.NewGuid());

        Assert.NotNull(updated);
        Assert.Equal("https://peer2.example.com", updated!.PlatformApiUrl);
        Assert.Equal("Peer 2", updated.DisplayName);
        var after = await _db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == peer.Id);
        Assert.Equal(before.ApiKeyProtected, after.ApiKeyProtected);
        Assert.Equal("ksh_****pe3r", updated.ApiKeyMask);
    }

    [Theory]
    [InlineData("ukz_")]
    [InlineData("kz_")]
    [InlineData("ksh_")]
    public async Task UpdateAsync_ById_RotatesKeyWithItsRealPrefix(string prefix)
    {
        var service = CreateService();
        var (cloud, peer) = await SeedTwoAsync(service);
        var apiKey = prefix + "ROTATEDPEER1234";

        var updated = await service.UpdateAsync(peer.Id,
            new UpsertPlatformConnectionRequest(PeerUrl, "Peer", apiKey), Guid.NewGuid());

        Assert.Equal(prefix + "****1234", updated!.ApiKeyMask);
        Assert.Equal(prefix + "1234", (await _db.PlatformConnections.SingleAsync(c => c.Id == peer.Id)).ApiKeyLast4);
        Assert.Equal(updated.ApiKeyMask, (await service.GetByIdAsync(peer.Id))!.ApiKeyMask);
        Assert.Equal("ukz_****cl0d", (await service.GetByIdAsync(cloud.Id))!.ApiKeyMask);
        Assert.Equal(apiKey, (await service.ResolveForOutboundCallAsync(peer.Id))!.Value.ApiKeyPlaintext);
    }

    [Fact]
    public async Task UpdateAsync_ById_ToUrlOfAnotherConnection_Throws()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(peer.Id, new UpsertPlatformConnectionRequest(CloudUrl, null, null), Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateAsync_ById_NotFound_ReturnsNull()
    {
        var service = CreateService();
        Assert.Null(await service.UpdateAsync(Guid.NewGuid(),
            new UpsertPlatformConnectionRequest(PeerUrl, null, PeerKey), Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteByIdAsync_Blocked_WhenLinksReference()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);
        _db.VaultSyncLinks.Add(new VaultSyncLink
        {
            LocalVaultId = Guid.NewGuid(),
            RemoteVaultId = Guid.NewGuid(),
            PlatformConnectionId = peer.Id,
        });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteByIdAsync(peer.Id));
        Assert.Equal(2, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task DeleteByIdAsync_RemovesOnlyThatConnection()
    {
        var service = CreateService();
        var (cloud, peer) = await SeedTwoAsync(service);

        Assert.True(await service.DeleteByIdAsync(peer.Id));

        var remaining = await _db.PlatformConnections.Select(c => c.Id).ToListAsync();
        Assert.Equal(new[] { cloud.Id }, remaining);
    }

    [Fact]
    public async Task DeleteByIdAsync_NotFoundOrOtherTenant_ReturnsFalse()
    {
        var service = CreateService();
        var (_, peer) = await SeedTwoAsync(service);

        Assert.False(await service.DeleteByIdAsync(Guid.NewGuid()));

        _tenantProvider.TenantId.Returns(TenantB);
        Assert.False(await service.DeleteByIdAsync(peer.Id));
        Assert.Equal(2, await _db.PlatformConnections.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task TestByIdAsync_CallsThatConnectionsHost_AndRecordsResultOnThatRow()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{}");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var service = CreateService(factory);
        var (cloud, peer) = await SeedTwoAsync(service);

        var result = await service.TestByIdAsync(peer.Id);

        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, result.Status);
        Assert.Equal("peer.example.com", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal(PeerKey, handler.LastRequest.Headers.GetValues("X-Api-Key").Single());

        var peerRow = await _db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == peer.Id);
        var cloudRow = await _db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == cloud.Id);
        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, peerRow.LastTestStatus);
        Assert.Equal(PlatformConnectionTestStatus.Untested, cloudRow.LastTestStatus);
    }

    [Fact]
    public async Task TestByIdAsync_NotFound_ReturnsUntested()
    {
        var service = CreateService();
        var result = await service.TestByIdAsync(Guid.NewGuid());
        Assert.Equal(PlatformConnectionTestStatus.Untested, result.Status);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public RecordingHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
