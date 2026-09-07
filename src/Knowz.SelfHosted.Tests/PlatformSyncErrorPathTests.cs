using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
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
/// Error-path / edge-case coverage for the platform sync stack.
///
/// Happy path and 401/403 sanitization are covered in <c>PlatformBrowsingTests</c>,
/// <c>PlatformSyncItemOpsTests</c>, <c>PlatformConnectionServiceTests</c>, and
/// <c>PlatformAuditLogTests</c>. This file fills gaps around:
/// - PlatformSyncClient: timeouts, network failures, malformed JSON, empty responses,
///   truncation, 404/500 handling, and Guid.Empty guards.
/// - PlatformConnectionService: URL validator rejection, 401 test audit, delete-blocked
///   conflict, missing-connection resolve.
/// - PlatformAuditLogService: sanitization edge cases (multi-redaction, auth headers,
///   basic-auth URLs, truncation).
/// - PlatformSyncRateLimiter: zero item count, double-complete safety, isolation.
/// </summary>
public class PlatformSyncErrorPathTests : IDisposable
{
    private const string BaseUrl = "https://api.test.knowz.io";
    private const string ApiKey = "ukz_test_abcdef1234567890";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;

    public PlatformSyncErrorPathTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantA);
        _db = new SelfHostedDbContext(options, _tenantProvider);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // PlatformSyncClient error paths — GetSchemaAsync / Import / ExportItem
    // =========================================================================

    [Fact]
    public async Task GetSchemaAsync_PlatformTimeout_ThrowsWithoutLeakingHeaderDetails()
    {
        // EnsureSuccessStatusCode never sees a response — HttpClient surfaces the cancel.
        // What we care about: the exception doesn't echo back the X-Api-Key header.
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.")
        };
        var (client, link) = BuildClient(handler);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetSchemaAsync(link));
        Assert.DoesNotContain(ApiKey, ex.ToString());
    }

    [Fact]
    public async Task GetSchemaAsync_NetworkFailure_ThrowsWithoutLeakingApiKey()
    {
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => throw new HttpRequestException("Connection refused")
        };
        var (client, link) = BuildClient(handler);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetSchemaAsync(link));
        Assert.DoesNotContain(ApiKey, ex.ToString());
    }

    [Fact]
    public async Task ListPlatformKnowledgeAsync_MalformedJson_ThrowsBrowseException()
    {
        // V-SEC-13: malformed platform JSON must not leak a stack trace to the caller.
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not valid json at all", Encoding.UTF8, "application/json")
            }
        };
        var (client, _) = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(() =>
            client.ListPlatformKnowledgeAsync(BaseUrl, ApiKey, Guid.NewGuid(), 1, 25));

        Assert.Equal(PlatformBrowseErrorKind.Unreachable, ex.Kind);
        Assert.Equal("Failed to fetch from platform", ex.Message);
    }

    [Fact]
    public async Task ListPlatformKnowledgeAsync_EmptyDataArray_HandlesCleanly()
    {
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => Json(HttpStatusCode.OK, new { success = true, data = Array.Empty<object>() })
        };
        var (client, _) = BuildClient(handler);

        var result = await client.ListPlatformKnowledgeAsync(BaseUrl, ApiKey, Guid.NewGuid(), 1, 25);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(25, result.PageSize);
    }

    [Fact]
    public async Task GetPlatformKnowledgeAsync_ContentExceeds2000Chars_TruncatesWithSuffix()
    {
        var id = Guid.NewGuid();
        var hugeContent = new string('x', 5000);
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => Json(HttpStatusCode.OK, new
            {
                success = true,
                data = new { id = id.ToString(), title = "T", content = hugeContent }
            })
        };
        var (client, _) = BuildClient(handler);

        var result = await client.GetPlatformKnowledgeAsync(BaseUrl, ApiKey, id);

        Assert.NotNull(result.Content);
        // 2000 content chars + "... (truncated)" suffix.
        Assert.Equal(2000 + "... (truncated)".Length, result.Content!.Length);
        Assert.EndsWith("... (truncated)", result.Content);
    }

    [Fact]
    public async Task ExportItemAsync_Platform404_ReturnsNull()
    {
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        };
        var (client, link) = BuildClient(handler);

        var result = await client.ExportItemAsync(link, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ExportItemAsync_PlatformReturnsWrongGuid_ThrowsInvalidData()
    {
        // V-SEC-12: the package was valid JSON but the KnowledgeItems contained Guid.Empty —
        // the client must reject before the orchestrator ever sees it.
        var package = new PortableExportPackage
        {
            SchemaVersion = CoreSchema.Version,
            SourceEdition = "platform",
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata { TotalKnowledgeItems = 1 },
            Data = new PortableExportData
            {
                KnowledgeItems = new List<PortableKnowledge>
                {
                    new() { Id = Guid.Empty, Title = "Bad", Content = "x" }
                }
            }
        };
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => Json(HttpStatusCode.OK, new { success = true, data = package })
        };
        var (client, link) = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExportItemAsync(link, Guid.NewGuid()));
        Assert.Contains("invalid data", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportDeltaAsync_PlatformReturns500_ThrowsWithoutEchoingBody()
    {
        // EnsureSuccessStatusCode wraps 5xx as HttpRequestException — verify the response body
        // (which could carry internal details) is not echoed into the exception message.
        const string internalBody =
            "System.NullReferenceException at Deep.Internal.Stuff() with secret config dump";
        var handler = new ErrorPathMockHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(internalBody, Encoding.UTF8, "application/json")
            }
        };
        var (client, link) = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ImportDeltaAsync(link, EmptyPackage()));

        Assert.DoesNotContain("NullReferenceException", ex.Message);
        Assert.DoesNotContain("secret config dump", ex.Message);
    }

    // =========================================================================
    // PlatformConnectionService error paths
    // =========================================================================

    [Fact]
    public async Task UpsertAsync_UrlValidatorRejects_WritesFailedAuditEntry()
    {
        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(false, "Host not on allowlist",
                UrlValidationErrorCode.NotAllowlisted));

        var auditLog = Substitute.For<IPlatformAuditLog>();
        var service = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(),
            urlValidator, Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PlatformConnectionService>>(),
            auditLog);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertAsync(
                new UpsertPlatformConnectionRequest("http://attacker.com", null, ApiKey),
                Guid.NewGuid()));

        // Audit call was made with Failed status — the actual row assertion is in the audit test.
        await auditLog.Received(1).LogAsync(
            Arg.Is<PlatformSyncRunStart>(s => s.Operation == PlatformSyncOperation.Connect),
            PlatformSyncRunStatus.Failed,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestAsync_PlatformReturns401_WritesFailedAuditEntry()
    {
        var urlValidator = StubOkUrlValidator();
        var auditLog = Substitute.For<IPlatformAuditLog>();

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformSync").Returns(_ =>
            new HttpClient(new ErrorPathMockHandler
            {
                ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"bad token details\"}")
                }
            }));

        var service = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(),
            urlValidator, factory,
            Substitute.For<ILogger<PlatformConnectionService>>(),
            auditLog);

        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest(BaseUrl, null, ApiKey), Guid.NewGuid());

        var result = await service.TestAsync();

        Assert.Equal(PlatformConnectionTestStatus.Unauthorized, result.Status);
        // Connect upsert + Failed test = 2 audit calls. Verify the Failed one.
        await auditLog.Received().LogAsync(
            Arg.Is<PlatformSyncRunStart>(s => s.Operation == PlatformSyncOperation.TestConnection),
            PlatformSyncRunStatus.Failed,
            Arg.Is<string?>(m => m != null && !m.Contains("bad token details")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_LinksStillExist_WritesFailedAuditEntry()
    {
        var urlValidator = StubOkUrlValidator();
        var auditLog = Substitute.For<IPlatformAuditLog>();
        var service = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(),
            urlValidator, Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PlatformConnectionService>>(),
            auditLog);

        await service.UpsertAsync(
            new UpsertPlatformConnectionRequest(BaseUrl, null, ApiKey), Guid.NewGuid());
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
        await auditLog.Received(1).LogAsync(
            Arg.Is<PlatformSyncRunStart>(s => s.Operation == PlatformSyncOperation.Disconnect),
            PlatformSyncRunStatus.Failed,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveForOutboundCallAsync_NoConnectionRow_ReturnsNull()
    {
        // No row seeded for TenantA — should NOT throw and must NOT leak a tenant id in any
        // exception message. The method returns null so the caller can surface a generic error.
        var urlValidator = StubOkUrlValidator();
        var service = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(),
            urlValidator, Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PlatformConnectionService>>());

        var result = await service.ResolveForOutboundCallAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task TryLogAuditAsync_AuditLogThrows_SwallowedByWrapper()
    {
        // The documented fire-and-forget boundary lives in PlatformConnectionService — audit
        // failures must not bubble up into the primary Connect/Disconnect/Test call.
        var urlValidator = StubOkUrlValidator();
        var auditLog = Substitute.For<IPlatformAuditLog>();
        auditLog.LogAsync(
                Arg.Any<PlatformSyncRunStart>(),
                Arg.Any<PlatformSyncRunStatus>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("DB is on fire"));

        var service = new PlatformConnectionService(
            _db, _tenantProvider, new EphemeralDataProtectionProvider(),
            urlValidator, Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PlatformConnectionService>>(),
            auditLog);

        // Upsert still succeeds — the audit failure is logged and discarded.
        var dto = await service.UpsertAsync(
            new UpsertPlatformConnectionRequest(BaseUrl, null, ApiKey),
            Guid.NewGuid());

        Assert.NotNull(dto);
        Assert.Equal(1, await _db.PlatformConnections.CountAsync());
    }

    // =========================================================================
    // PlatformAuditLogService sanitization edge cases
    // =========================================================================

    [Fact]
    public async Task FailAsync_ErrorContainsMultipleApiKeys_RedactsAllOccurrences()
    {
        var service = BuildAuditService();
        var runId = await service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await service.FailAsync(runId,
            "Failed: ukz_abc123xyz789 and fallback ukz_def456uvw012");

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.DoesNotContain("ukz_abc123xyz789", row.ErrorMessage);
        Assert.DoesNotContain("ukz_def456uvw012", row.ErrorMessage);
        Assert.Contains("REDACTED", row.ErrorMessage);
    }

    [Fact]
    public async Task FailAsync_XApiKeyHeaderInError_RedactedFirstToken()
    {
        // The sanitizer regex captures `X-Api-Key: <value>` through end-of-line, so any
        // single-token secret is fully redacted and the header name is preserved.
        var service = BuildAuditService();
        var runId = await service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await service.FailAsync(runId,
            "Platform responded 401 Unauthorized with X-Api-Key: secretheadervalueXYZ");

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.DoesNotContain("secretheadervalueXYZ", row.ErrorMessage);
        Assert.Contains("[redacted]", row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailAsync_AuthHeaderWithJwtValue_FullySanitized()
    {
        // Regression guard: prior regex only redacted the first whitespace-delimited token,
        // so "Authorization: Bearer eyJ..." leaked the JWT body. The updated greedy match
        // must strip the entire value (multi-word / JWT-style) up to the next newline.
        var service = BuildAuditService();
        var runId = await service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await service.FailAsync(runId,
            "Request failed with Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature here");

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        // No part of the JWT (header, payload, signature) leaks through.
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", row.ErrorMessage);
        Assert.DoesNotContain("payload.signature", row.ErrorMessage);
        Assert.DoesNotContain("Bearer", row.ErrorMessage);
        // Header name preserved for debuggability.
        Assert.Contains("Authorization", row.ErrorMessage);
        // Redaction marker present.
        Assert.Contains("[redacted]", row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailAsync_BasicAuthInUrl_Redacted()
    {
        var service = BuildAuditService();
        var runId = await service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await service.FailAsync(runId,
            "Connection failed to https://admin:s3cretP4ss@api.knowz.io/api/v1/sync/schema");

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.DoesNotContain("admin:s3cretP4ss", row.ErrorMessage);
        Assert.DoesNotContain("s3cretP4ss", row.ErrorMessage);
    }

    [Fact]
    public async Task FailAsync_ErrorExceeds500Chars_TruncatedTo500()
    {
        var service = BuildAuditService();
        var runId = await service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        var huge = new string('q', 2000);
        await service.FailAsync(runId, huge);

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.Equal(500, row.ErrorMessage!.Length);
    }

    // =========================================================================
    // PlatformSyncRateLimiter edge cases
    // =========================================================================

    [Fact]
    public async Task RateLimiter_ZeroItemCount_Allowed()
    {
        // itemCount=0 is a degenerate but legitimate input (e.g. a noop dry-run probe).
        // It must not trip the 100-item cap.
        var limiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        var decision = await limiter.CheckAsync(Guid.NewGuid(), itemCount: 0);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public async Task RateLimiter_DoubleComplete_SafeAndIdempotent()
    {
        var limiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        var opId = await limiter.RecordOperationAsync(TenantA, "first");
        await limiter.CompleteOperationAsync(opId);
        // Second completion must not throw. This guards against retry loops that call
        // Complete in a try/finally + a catch handler.
        await limiter.CompleteOperationAsync(opId);

        // After double-complete the tenant can still start new ops (concurrency slot free).
        var decision = await limiter.CheckAsync(TenantA, 1);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task RateLimiter_CompleteUnknownOperationId_SafeNoOp()
    {
        var limiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        // An opId that was never recorded — calling Complete on it must not crash.
        await limiter.CompleteOperationAsync(Guid.NewGuid());

        var decision = await limiter.CheckAsync(TenantA, 1);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task RateLimiter_TenantsIsolated_QuotaDoesNotBleedAcrossTenants()
    {
        var limiter = new PlatformSyncRateLimiter(
            Substitute.For<ILogger<PlatformSyncRateLimiter>>());

        // Fill tenant A's hourly quota, completing each op so concurrency is free.
        for (var i = 0; i < 10; i++)
        {
            var opId = await limiter.RecordOperationAsync(TenantA, $"fill-{i}");
            await limiter.CompleteOperationAsync(opId);
        }

        var aDecision = await limiter.CheckAsync(TenantA, 1);
        var bDecision = await limiter.CheckAsync(TenantB, 1);

        Assert.False(aDecision.Allowed);
        Assert.Equal(RateLimitReason.HourlyQuotaExceeded, aDecision.Reason);
        Assert.True(bDecision.Allowed);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private (PlatformSyncClient Client, VaultSyncLink Link) BuildClient(ErrorPathMockHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformSync").Returns(_ => new HttpClient(handler));

        var urlValidator = StubOkUrlValidator();
        var client = new PlatformSyncClient(
            factory,
            new EphemeralDataProtectionProvider(),
            urlValidator,
            _db,
            Substitute.For<ILogger<PlatformSyncClient>>());

#pragma warning disable CS0618 // Legacy fields used deliberately — no PlatformConnection row is needed for these tests.
        var link = new VaultSyncLink
        {
            Id = Guid.NewGuid(),
            LocalVaultId = Guid.NewGuid(),
            RemoteVaultId = Guid.NewGuid(),
            RemoteTenantId = Guid.NewGuid(),
            PlatformConnectionId = null,
            PlatformApiUrl = BaseUrl,
            ApiKeyEncrypted = ApiKey,
        };
#pragma warning restore CS0618

        return (client, link);
    }

    private PlatformAuditLogService BuildAuditService() =>
        new(_db, _tenantProvider, Substitute.For<ILogger<PlatformAuditLogService>>());

    private static IUrlValidator StubOkUrlValidator()
    {
        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(true, null));
        return urlValidator;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private static PortableExportPackage EmptyPackage() => new()
    {
        SchemaVersion = CoreSchema.Version,
        SourceEdition = "selfhosted",
        ExportedAt = DateTime.UtcNow,
        Metadata = new PortableExportMetadata { TotalKnowledgeItems = 0 },
        Data = new PortableExportData { KnowledgeItems = new List<PortableKnowledge>() }
    };

    /// <summary>
    /// Minimal HttpMessageHandler that delegates every request to a factory delegate,
    /// allowing tests to throw exceptions or return arbitrary responses per-request.
    /// </summary>
    private sealed class ErrorPathMockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseFactory is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(ResponseFactory(request));
        }
    }
}
