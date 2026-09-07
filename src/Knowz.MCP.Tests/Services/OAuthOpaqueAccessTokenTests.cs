using FluentAssertions;
using Knowz.MCP.Middleware;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// Tests for SEC_McpOAuthOpaqueAccessToken — every OAuth grant (authorization_code,
/// refresh_token, client_credentials via IssueOpaqueAccessToken) returns an opaque,
/// revocable session-store token instead of the raw platform API key, and
/// McpAuthMiddleware resolves that token back to the API key on subsequent MCP calls.
/// </summary>
public class OAuthOpaqueAccessTokenTests
{
    private const string ApiKey = "ukz_test_api_key_12345678";
    private const string RedirectUri = "http://localhost:8080/callback";

    private readonly McpSessionStore _sessionStore;
    private readonly OAuthService _service;

    public OAuthOpaqueAccessTokenTests()
    {
        _sessionStore = new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object);
        _service = new OAuthService(CreateWorkingCacheMock().Object, _sessionStore,
            new Mock<ILogger<OAuthService>>().Object);
    }

    // authorization_code grant returns an opaque token, never the raw API key
    [Fact]
    public void AuthorizationCodeGrant_ReturnsOpaqueToken_NotApiKey()
    {
        var result = RunAuthorizationCodeGrant("test-verifier");

        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe(ApiKey);
        result.AccessToken.Should().NotStartWith("kz_").And.NotStartWith("ukz_").And.NotStartWith("ksh_");
        _sessionStore.GetApiKey(result.AccessToken).Should().Be(ApiKey,
            "the opaque token must map back to the API key for downstream MCP auth");
    }

    // The opaque token authenticates a subsequent MCP request end-to-end via McpAuthMiddleware
    [Fact]
    public async Task OpaqueToken_FromAuthorizationCodeGrant_AuthenticatesMcpRequest()
    {
        var tokenResult = RunAuthorizationCodeGrant("test-verifier")!;

        var context = CreateMcpRequestContext(tokenResult.AccessToken);
        var nextCalled = false;
        var middleware = new McpAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            new Mock<ILogger<McpAuthMiddleware>>().Object,
            new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context, _sessionStore);

        nextCalled.Should().BeTrue("a valid opaque token must pass MCP auth");
        context.Items["ApiKey"].Should().Be(ApiKey, "middleware must resolve the opaque token to the raw API key");
    }

    // An unknown/revoked opaque token is rejected with 401
    [Fact]
    public async Task UnknownOpaqueToken_IsRejectedWith401()
    {
        var context = CreateMcpRequestContext("deadbeef00000000deadbeef00000000deadbeef00000000deadbeef00000000");
        var nextCalled = false;
        var middleware = new McpAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            new Mock<ILogger<McpAuthMiddleware>>().Object,
            new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context, _sessionStore);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
        context.Items["ApiKey"].Should().BeNull();
    }

    // Revoking the session-store entry invalidates the opaque token
    [Fact]
    public async Task RevokedOpaqueToken_NoLongerAuthenticates()
    {
        var tokenResult = RunAuthorizationCodeGrant("test-verifier")!;
        _sessionStore.RemoveSession(tokenResult.AccessToken);

        var context = CreateMcpRequestContext(tokenResult.AccessToken);
        var nextCalled = false;
        var middleware = new McpAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            new Mock<ILogger<McpAuthMiddleware>>().Object,
            new ConfigurationBuilder().Build());

        await middleware.InvokeAsync(context, _sessionStore);

        nextCalled.Should().BeFalse("a revoked opaque token must no longer authenticate");
        context.Response.StatusCode.Should().Be(401);
    }

    // refresh_token grant mints a FRESH opaque token (distinct from the first)
    [Fact]
    public void RefreshTokenGrant_MintsFreshOpaqueToken()
    {
        var first = RunAuthorizationCodeGrant("test-verifier")!;

        var refreshed = _service.ExchangeRefreshToken(first.RefreshToken!);

        refreshed.Should().NotBeNull();
        refreshed!.AccessToken.Should().NotBe(ApiKey);
        refreshed.AccessToken.Should().NotBe(first.AccessToken, "refresh must mint a new opaque token");
        _sessionStore.GetApiKey(refreshed.AccessToken).Should().Be(ApiKey);
    }

    // IssueOpaqueAccessToken (client_credentials path) uses the same scheme
    [Fact]
    public void IssueOpaqueAccessToken_MintsResolvableToken()
    {
        var token = _service.IssueOpaqueAccessToken(ApiKey);

        token.Should().NotBe(ApiKey);
        _sessionStore.GetApiKey(token).Should().Be(ApiKey);
    }

    // expires_in reflects the session store's real timeout, not an advisory constant
    [Fact]
    public void ExpiresIn_MatchesSessionStoreTimeout()
    {
        var result = RunAuthorizationCodeGrant("test-verifier")!;

        result.ExpiresIn.Should().Be((int)_sessionStore.SessionTimeout.TotalSeconds);
        _service.AccessTokenExpirySeconds.Should().Be((int)_sessionStore.SessionTimeout.TotalSeconds);
    }

    // PKCE non-regression: a wrong verifier still fails the exchange and mints nothing
    [Fact]
    public void AuthorizationCodeGrant_WrongPkceVerifier_StillRejected()
    {
        var request = _service.CreateAuthorizationRequest(
            "test-client", RedirectUri, "mcp:read",
            "test-state", CreateS256Challenge("correct-verifier"), "S256");
        var code = _service.CompleteAuthorization(request.RequestId, ApiKey);

        var result = _service.ExchangeCode(code, "wrong-verifier", RedirectUri);

        result.Should().BeNull("PKCE validation must not regress with opaque tokens");
    }

    private TokenResult? RunAuthorizationCodeGrant(string verifier)
    {
        var request = _service.CreateAuthorizationRequest(
            "test-client", RedirectUri, "mcp:read mcp:write",
            "test-state", CreateS256Challenge(verifier), "S256");
        var code = _service.CompleteAuthorization(request.RequestId, ApiKey);
        return _service.ExchangeCode(code, verifier, RedirectUri);
    }

    private static DefaultHttpContext CreateMcpRequestContext(string bearerToken)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Request.Path = "/mcp";
        context.Request.Headers["Authorization"] = $"Bearer {bearerToken}";
        return context;
    }

    private static Mock<IDistributedCache> CreateWorkingCacheMock()
    {
        var store = new Dictionary<string, byte[]>();
        var mock = new Mock<IDistributedCache>();

        mock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, _) => store[key] = value);

        mock.Setup(c => c.Get(It.IsAny<string>()))
            .Returns<string>(key => store.TryGetValue(key, out var val) ? val : null);

        mock.Setup(c => c.Remove(It.IsAny<string>()))
            .Callback<string>(key => store.Remove(key));

        return mock;
    }

    private static string CreateS256Challenge(string verifier)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
