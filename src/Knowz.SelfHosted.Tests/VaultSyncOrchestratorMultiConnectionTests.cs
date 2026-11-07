using System.Net;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
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
/// ADR_G6 (#1043): <see cref="VaultSyncOrchestrator.EstablishLinkAsync"/> binds a link to the
/// requested connection (by id, then by URL, then create) instead of "whichever connection the
/// tenant already has".
/// </summary>
public class VaultSyncOrchestratorMultiConnectionTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string CloudUrl = "https://api.knowz.io";
    private const string PeerUrl = "https://peer.example.com";

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IPlatformSyncClient _platformClient;
    private readonly PlatformConnectionService _connectionService;
    private readonly VaultSyncOrchestrator _orchestrator;

    public VaultSyncOrchestratorMultiConnectionTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, _tenantProvider);

        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.ValidatePlatformUrl(Arg.Any<string>()).Returns(new UrlValidationResult(true, null));

        // Connection test → 401 so the post-link probe takes its warning path without network.
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(HttpStatusCode.Unauthorized)));

        _connectionService = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(), urlValidator, factory,
            Substitute.For<ILogger<PlatformConnectionService>>());

        _platformClient = Substitute.For<IPlatformSyncClient>();
        _orchestrator = new VaultSyncOrchestrator(
            _db, _tenantProvider, _platformClient,
            null!, null!, Substitute.For<ILogger<VaultSyncOrchestrator>>(),
            connectionService: _connectionService);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task<Vault> SeedVaultAsync()
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Local" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync();
        return vault;
    }

    private Task<PlatformConnectionDto> SeedConnectionAsync(string url, string key) =>
        _connectionService.CreateAsync(new UpsertPlatformConnectionRequest(url, null, key), Guid.NewGuid());

    [Fact]
    public async Task EstablishLink_WithPlatformConnectionId_BindsThatConnection_NotThePrimary()
    {
        var cloud = await SeedConnectionAsync(CloudUrl, "ukz_cloudcloudcloudcloud0001");
        var peer = await SeedConnectionAsync(PeerUrl, "ksh_peerpeerpeerpeerpeer0002");
        var vault = await SeedVaultAsync();

        var dto = await _orchestrator.EstablishLinkAsync(new EstablishSyncLinkRequest
        {
            LocalVaultId = vault.Id,
            RemoteVaultId = Guid.NewGuid(),
            PlatformConnectionId = peer.Id,
        });

        var link = await _db.VaultSyncLinks.SingleAsync(l => l.Id == dto.LinkId);
        Assert.Equal(peer.Id, link.PlatformConnectionId);
        Assert.NotEqual(cloud.Id, link.PlatformConnectionId);
        Assert.Equal(PeerUrl, dto.PlatformApiUrl);
        Assert.Equal(2, await _db.PlatformConnections.CountAsync()); // nothing created
    }

    [Fact]
    public async Task EstablishLink_WithForeignConnectionId_Throws_AndCreatesNoLink()
    {
        var vault = await SeedVaultAsync();
        var foreign = new PlatformConnection
        {
            TenantId = OtherTenantId, PlatformApiUrl = PeerUrl, ApiKeyProtected = "x", CreatedByUserId = Guid.Empty,
        };
        _db.PlatformConnections.Add(foreign);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.EstablishLinkAsync(new EstablishSyncLinkRequest
        {
            LocalVaultId = vault.Id,
            RemoteVaultId = Guid.NewGuid(),
            PlatformConnectionId = foreign.Id,
        }));
        Assert.Equal(0, await _db.VaultSyncLinks.CountAsync());
    }

    [Fact]
    public async Task EstablishLink_WithoutConnectionId_MatchesExistingByNormalizedUrl()
    {
        await SeedConnectionAsync(CloudUrl, "ukz_cloudcloudcloudcloud0001");
        var peer = await SeedConnectionAsync(PeerUrl, "ksh_peerpeerpeerpeerpeer0002");
        var vault = await SeedVaultAsync();

        var dto = await _orchestrator.EstablishLinkAsync(new EstablishSyncLinkRequest
        {
            LocalVaultId = vault.Id,
            RemoteVaultId = Guid.NewGuid(),
            PlatformApiUrl = PeerUrl + "/",
            ApiKey = "ksh_ignored-because-url-already-known",
        });

        var link = await _db.VaultSyncLinks.SingleAsync(l => l.Id == dto.LinkId);
        Assert.Equal(peer.Id, link.PlatformConnectionId);
        Assert.Equal(2, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task EstablishLink_WithoutConnectionId_NewUrlAndKey_CreatesSecondConnection()
    {
        var cloud = await SeedConnectionAsync(CloudUrl, "ukz_cloudcloudcloudcloud0001");
        var vault = await SeedVaultAsync();

        var dto = await _orchestrator.EstablishLinkAsync(new EstablishSyncLinkRequest
        {
            LocalVaultId = vault.Id,
            RemoteVaultId = Guid.NewGuid(),
            PlatformApiUrl = PeerUrl,
            ApiKey = "ksh_peerpeerpeerpeerpeer0002",
        });

        var link = await _db.VaultSyncLinks.SingleAsync(l => l.Id == dto.LinkId);
        Assert.NotNull(link.PlatformConnectionId);
        Assert.NotEqual(cloud.Id, link.PlatformConnectionId);
        var created = await _db.PlatformConnections.SingleAsync(c => c.Id == link.PlatformConnectionId);
        Assert.Equal(PeerUrl, created.PlatformApiUrl);
        Assert.Equal(2, await _db.PlatformConnections.CountAsync());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StubHandler(HttpStatusCode status) { _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
    }
}
