using System.Net;
using System.Text.Json;
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
/// Coverage for <see cref="PlatformConnectionService"/> — encryption, masking, URL validation,
/// delete-blocked-by-links, cross-tenant isolation, sanitized test errors.
/// </summary>
public class PlatformConnectionServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string SampleApiKey = "ukz_ABCD1234EFGH5678IJKLa9F2";

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IDataProtectionProvider _dpp;
    private readonly IUrlValidator _urlValidator;
    private readonly ILogger<PlatformConnectionService> _logger;

    public PlatformConnectionServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantA);

        _db = new SelfHostedDbContext(options, _tenantProvider);
        _dpp = new EphemeralDataProtectionProvider();
        _urlValidator = Substitute.For<IUrlValidator>();
        _urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(true, null));

        _logger = Substitute.For<ILogger<PlatformConnectionService>>();
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
            _db, _tenantProvider, _dpp, _urlValidator, factory, _logger);
    }

    [Fact]
    public async Task UpsertAsync_NewRow_EncryptsKey_AndStoresLast4()
    {
        var service = CreateService();
        var req = new UpsertPlatformConnectionRequest("https://api.knowz.io", "Prod", SampleApiKey);

        var dto = await service.UpsertAsync(req, createdByUserId: Guid.NewGuid());

        var row = await _db.PlatformConnections.SingleAsync();
        Assert.NotEqual(SampleApiKey, row.ApiKeyProtected); // ciphertext != plaintext
        Assert.True(row.ApiKeyProtected.Length > SampleApiKey.Length);
        Assert.Equal(SampleApiKey[^4..], row.ApiKeyLast4);
        Assert.Equal("https://api.knowz.io", row.PlatformApiUrl);
        Assert.Equal(TenantA, row.TenantId);

        Assert.Equal("ukz_****a9F2", dto.ApiKeyMask);
        Assert.True(dto.HasApiKey);
    }

    [Fact]
    public async Task GetAsync_ReturnsMaskedKey_Only()
    {
        var service = CreateService();
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());

        var dto = await service.GetAsync();

        Assert.NotNull(dto);
        Assert.True(dto!.HasApiKey);
        Assert.Equal("ukz_****a9F2", dto.ApiKeyMask);

        // Serialized response must never contain the plaintext key, the ciphertext, or any of
        // the first 8 characters of the key.
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(SampleApiKey, json);
        Assert.DoesNotContain("ABCD1234", json);
        Assert.DoesNotContain("ApiKeyProtected", json);
        Assert.DoesNotContain("apiKeyProtected", json);
    }

    [Fact]
    public async Task UpsertAsync_PartialUpdate_KeepsExistingCiphertext_WhenApiKeyNull()
    {
        var service = CreateService();
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());
        var originalRow = await _db.PlatformConnections.AsNoTracking().SingleAsync();

        // Partial update: only change DisplayName, leave ApiKey null.
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", "New Label", ApiKey: null),
            createdByUserId: Guid.NewGuid());

        var updated = await _db.PlatformConnections.AsNoTracking().SingleAsync();
        Assert.Equal("New Label", updated.DisplayName);
        Assert.Equal(originalRow.ApiKeyProtected, updated.ApiKeyProtected);
    }

    [Fact]
    public async Task UpsertAsync_InvalidUrl_Throws()
    {
        _urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(false, "blocked", UrlValidationErrorCode.NotAllowlisted));
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertAsync(
                new UpsertPlatformConnectionRequest("https://evil.com", null, SampleApiKey),
                Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAsync_Blocks_WhenLinksReference()
    {
        var service = CreateService();
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());
        var connection = await _db.PlatformConnections.SingleAsync();

        _db.VaultSyncLinks.Add(new VaultSyncLink
        {
            LocalVaultId = Guid.NewGuid(),
            RemoteVaultId = Guid.NewGuid(),
            PlatformConnectionId = connection.Id,
        });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync());
        Assert.Equal(1, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_WhenNoLinks()
    {
        var service = CreateService();
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());

        await service.DeleteAsync();

        Assert.Equal(0, await _db.PlatformConnections.CountAsync());
    }

    [Fact]
    public async Task CrossTenant_CiphertextCannotBeDecrypted_WithOtherTenantPurpose()
    {
        // Tenant A writes a row
        var svcA = CreateService();
        await svcA.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());
        var rowA = await _db.PlatformConnections.AsNoTracking().SingleAsync();

        // Attempt to decrypt with Tenant B's purpose — must throw CryptographicException
        var protectorB = _dpp
            .CreateProtector(PlatformConnectionService.MasterPurpose)
            .CreateProtector($"{PlatformConnectionService.MasterPurpose}.{TenantB}");

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => protectorB.Unprotect(rowA.ApiKeyProtected));
    }

    [Fact]
    public async Task TestAsync_Unauthorized_ReturnsGenericMessage()
    {
        var service = CreateServiceWithStubbedHttp(HttpStatusCode.Unauthorized,
            responseBody: "{\"error\":\"Platform says: secret details\"}");
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());

        var result = await service.TestAsync();

        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, result.Status);
        Assert.Equal(PlatformConnectionService.MsgUnauthorized, result.Message);
        Assert.DoesNotContain("secret details", result.Message ?? "");
    }

    [Fact]
    public async Task TestAsync_Forbidden_ReturnsUnauthorized_GenericMessage()
    {
        var service = CreateServiceWithStubbedHttp(HttpStatusCode.Forbidden, responseBody: "<html>oops</html>");
        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest("https://api.knowz.io", null, SampleApiKey),
            createdByUserId: Guid.NewGuid());

        var result = await service.TestAsync();

        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, result.Status);
        Assert.Equal(PlatformConnectionService.MsgUnauthorized, result.Message);
    }

    [Fact]
    public async Task TestCandidateAsync_DoesNotPersist_Row()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformSync").Returns(
            MakeStubbedHttpClient(HttpStatusCode.Unauthorized, "{}"));
        var service = CreateService(factory);

        Assert.Equal(0, await _db.PlatformConnections.CountAsync());
        var result = await service.TestCandidateAsync("https://api.knowz.io", "ukz_test");
        Assert.Equal(0, await _db.PlatformConnections.CountAsync());
        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, result.Status);
    }

    // ---------- helpers ----------

    private PlatformConnectionService CreateServiceWithStubbedHttp(
        HttpStatusCode status, string responseBody)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformSync").Returns(_ => MakeStubbedHttpClient(status, responseBody));
        return CreateService(factory);
    }

    private static HttpClient MakeStubbedHttpClient(HttpStatusCode status, string responseBody)
    {
        var handler = new StubHttpMessageHandler(status, responseBody);
        return new HttpClient(handler);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status; _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
