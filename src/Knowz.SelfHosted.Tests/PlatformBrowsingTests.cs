using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for the read-only platform browse proxy on <see cref="PlatformSyncClient"/>.
/// These verify response sanitization (V-SEC-12), error mapping (V-SEC-13), and the
/// zero-trust handling of the untrusted platform response body.
/// </summary>
public class PlatformBrowsingTests
{
    private const string BaseUrl = "https://api.test.knowz.io";
    private const string ApiKey = "test-api-key-123";

    private readonly ILogger<PlatformSyncClient> _logger;
    private readonly BrowseMockHttpMessageHandler _handler;
    private readonly PlatformSyncClient _client;
    private readonly SelfHostedDbContext _db;

    public PlatformBrowsingTests()
    {
        _logger = Substitute.For<ILogger<PlatformSyncClient>>();
        _handler = new BrowseMockHttpMessageHandler();

        var httpClient = new HttpClient(_handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("KnowzPlatformSync").Returns(httpClient);

        var dpp = new EphemeralDataProtectionProvider();
        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.ValidatePlatformUrl(Arg.Any<string>())
            .Returns(new UrlValidationResult(true, null));

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new SelfHostedDbContext(options, tenantProvider);

        _client = new PlatformSyncClient(factory, dpp, urlValidator, _db, _logger);
    }

    // --- ListPlatformVaultsAsync ---

    [Fact]
    public async Task ListPlatformVaults_ReturnsMappedDtos()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new object[]
            {
                new
                {
                    id = id1.ToString(),
                    name = "Engineering",
                    description = "Eng vault",
                    knowledgeCount = 42,
                    updatedAt = "2026-04-10T00:00:00Z"
                },
                new
                {
                    id = id2.ToString(),
                    name = "Design",
                    description = (string?)null,
                    knowledgeCount = 7,
                    updatedAt = "2026-04-09T10:00:00Z"
                }
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.ListPlatformVaultsAsync(BaseUrl, ApiKey);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Vaults.Count);
        Assert.Equal(id1, result.Vaults[0].Id);
        Assert.Equal("Engineering", result.Vaults[0].Name);
        Assert.Equal("Eng vault", result.Vaults[0].Description);
        Assert.Equal(42, result.Vaults[0].KnowledgeCount);
        Assert.Equal(id2, result.Vaults[1].Id);
        Assert.Null(result.Vaults[1].Description);

        // Verify request shape: correct path + header
        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Get, _handler.LastRequest!.Method);
        Assert.Contains("/api/v1/vaults", _handler.LastRequest.RequestUri!.PathAndQuery);
        Assert.Equal(ApiKey, _handler.LastRequest.Headers.GetValues("X-Api-Key").First());
    }

    [Fact]
    public async Task ListPlatformVaults_SkipsRowsWithInvalidGuid()
    {
        // V-SEC-12: reject path-traversal smuggled through the id field
        var goodId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new object[]
            {
                new { id = "../../../etc/passwd", name = "Evil", knowledgeCount = 0 },
                new { id = goodId.ToString(), name = "Good", knowledgeCount = 1 }
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.ListPlatformVaultsAsync(BaseUrl, ApiKey);

        Assert.Single(result.Vaults);
        Assert.Equal(goodId, result.Vaults[0].Id);
        Assert.Equal("Good", result.Vaults[0].Name);
    }

    [Fact]
    public async Task ListPlatformVaults_StripsUnknownFields()
    {
        // V-SEC-12: unknown fields in the platform response must not reach the DTO
        var id = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new object[]
            {
                new
                {
                    id = id.ToString(),
                    name = "X",
                    knowledgeCount = 0,
                    injectedField = "<script>alert('xss')</script>",
                    anotherInjection = new { nested = "evil" }
                }
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.ListPlatformVaultsAsync(BaseUrl, ApiKey);

        Assert.Single(result.Vaults);
        // DTO only exposes declared fields; extras are silently dropped.
        var vault = result.Vaults[0];
        Assert.Equal(id, vault.Id);
        Assert.Equal("X", vault.Name);
        Assert.Null(vault.Description);
    }

    [Fact]
    public async Task ListPlatformVaults_PlatformUnauthorized_ThrowsSanitizedException()
    {
        // V-SEC-13: platform 401 must become "Invalid platform API key", never the raw body
        _handler.SetResponse(HttpStatusCode.Unauthorized,
            "{\"success\":false,\"errors\":[\"Secret internal diag: token=AAAA\"]}");

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync(BaseUrl, ApiKey));

        Assert.Equal(PlatformBrowseErrorKind.Unauthorized, ex.Kind);
        Assert.Equal("Invalid platform API key", ex.Message);
        Assert.DoesNotContain("token=AAAA", ex.Message);
    }

    [Fact]
    public async Task ListPlatformVaults_PlatformForbidden_MapsToUnauthorized()
    {
        _handler.SetResponse(HttpStatusCode.Forbidden, "no peeking");

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync(BaseUrl, ApiKey));

        Assert.Equal(PlatformBrowseErrorKind.Unauthorized, ex.Kind);
        Assert.Equal("Invalid platform API key", ex.Message);
    }

    [Fact]
    public async Task ListPlatformVaults_Platform5xx_ThrowsGenericUnreachable()
    {
        // V-SEC-13: never leak the raw 5xx body / stack trace
        _handler.SetResponse(HttpStatusCode.InternalServerError,
            "System.NullReferenceException at Deep.Internal.Stuff()");

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync(BaseUrl, ApiKey));

        Assert.Equal(PlatformBrowseErrorKind.Unreachable, ex.Kind);
        Assert.Equal("Failed to fetch from platform", ex.Message);
        Assert.DoesNotContain("NullReferenceException", ex.Message);
    }

    [Fact]
    public async Task ListPlatformVaults_NetworkError_ThrowsUnreachable()
    {
        _handler.SetException(new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync(BaseUrl, ApiKey));

        Assert.Equal(PlatformBrowseErrorKind.Unreachable, ex.Kind);
        Assert.Equal("Failed to fetch from platform", ex.Message);
    }

    [Fact]
    public async Task ListPlatformVaults_MissingCredentials_ThrowsNotConfigured()
    {
        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync(BaseUrl, ""));
        Assert.Equal(PlatformBrowseErrorKind.NotConfigured, ex.Kind);

        var ex2 = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.ListPlatformVaultsAsync("", ApiKey));
        Assert.Equal(PlatformBrowseErrorKind.NotConfigured, ex2.Kind);
    }

    // --- ListPlatformKnowledgeAsync ---

    [Fact]
    public async Task ListPlatformKnowledge_ReturnsMappedPage()
    {
        var vaultId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new object[]
            {
                new
                {
                    id = itemId.ToString(),
                    title = "How to deploy",
                    summary = "A deployment runbook",
                    aiSummary = "ignored when summary is set",
                    updatedAt = "2026-04-10T00:00:00Z",
                    createdByUserName = "alice"
                }
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.ListPlatformKnowledgeAsync(
            BaseUrl, ApiKey, vaultId, page: 2, pageSize: 50);

        Assert.Equal(2, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Single(result.Items);
        Assert.Equal(itemId, result.Items[0].Id);
        Assert.Equal("How to deploy", result.Items[0].Title);
        Assert.Equal("A deployment runbook", result.Items[0].Summary);
        Assert.Equal("alice", result.Items[0].CreatedBy);

        Assert.Contains($"/api/v1/vaults/{vaultId}/knowledge", _handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Contains("page=2", _handler.LastRequest.RequestUri.Query);
        Assert.Contains("pageSize=50", _handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task ListPlatformKnowledge_FallsBackToAiSummaryWhenSummaryMissing()
    {
        var vaultId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new object[]
            {
                new
                {
                    id = itemId.ToString(),
                    title = "T",
                    summary = (string?)null,
                    aiSummary = "AI generated",
                    updatedAt = "2026-04-10T00:00:00Z"
                }
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.ListPlatformKnowledgeAsync(BaseUrl, ApiKey, vaultId, 1, 25);

        Assert.Equal("AI generated", result.Items[0].Summary);
    }

    // --- GetPlatformKnowledgeAsync ---

    [Fact]
    public async Task GetPlatformKnowledge_ReturnsDetailDto()
    {
        var id = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                id = id.ToString(),
                title = "Design doc",
                content = "Body of the document",
                summary = "A short summary",
                tags = "design,architecture",
                updatedAt = "2026-04-10T00:00:00Z",
                createdByUserName = "bob",
                injectedField = "should be stripped"
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var result = await _client.GetPlatformKnowledgeAsync(BaseUrl, ApiKey, id);

        Assert.Equal(id, result.Id);
        Assert.Equal("Design doc", result.Title);
        Assert.Equal("Body of the document", result.Content);
        Assert.Equal("A short summary", result.Summary);
        Assert.Equal("design,architecture", result.Tags);
        Assert.Equal("bob", result.CreatedBy);

        Assert.Contains($"/api/v1/knowledge/{id}", _handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetPlatformKnowledge_Platform404_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _handler.SetResponse(HttpStatusCode.NotFound, "nope");

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.GetPlatformKnowledgeAsync(BaseUrl, ApiKey, id));

        Assert.Equal(PlatformBrowseErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task GetPlatformKnowledge_ResponseMissingId_ThrowsNotFound()
    {
        // V-SEC-12: an adversarial response that omits the id must not be materialized.
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new { title = "Missing id" }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.GetPlatformKnowledgeAsync(BaseUrl, ApiKey, Guid.NewGuid()));

        Assert.Equal(PlatformBrowseErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task GetPlatformKnowledge_InvalidGuidInResponse_ThrowsNotFound()
    {
        // V-SEC-12: path-traversal in the id field must be rejected.
        var body = JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                id = "../../../etc/passwd",
                title = "Bad"
            }
        });
        _handler.SetResponse(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<PlatformBrowseException>(
            () => _client.GetPlatformKnowledgeAsync(BaseUrl, ApiKey, Guid.NewGuid()));

        Assert.Equal(PlatformBrowseErrorKind.NotFound, ex.Kind);
    }

    // --- Mock message handler ---

    private sealed class BrowseMockHttpMessageHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseContent = "";
        private Exception? _exception;

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _responseContent = content;
            _exception = null;
        }

        public void SetException(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_exception != null)
                throw _exception;

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
