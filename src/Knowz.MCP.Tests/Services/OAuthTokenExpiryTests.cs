using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// Tests for FIX_OAuthTokenExpiry — verifies expires_in is 2592000 (30 days).
/// </summary>
public class OAuthTokenExpiryTests
{
    private readonly OAuthService _service;
    private readonly McpSessionStore _sessionStore;

    public OAuthTokenExpiryTests()
    {
        var cacheMock = CreateWorkingCacheMock();
        var logger = new Mock<ILogger<OAuthService>>();
        _sessionStore = new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object);
        _service = new OAuthService(cacheMock.Object, _sessionStore, logger.Object);
    }

    // VERIFY-4: OAuthService.TokenExpirySeconds constant equals 2592000 (30 days)
    [Fact]
    public void TokenExpirySeconds_Equals_2592000()
    {
        OAuthService.TokenExpirySeconds.Should().Be(2592000);
    }

    // VERIFY-1: authorization_code grant returns expires_in: 2592000
    [Fact]
    public void ExchangeCode_Returns_ExpiresIn_2592000()
    {
        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", CreateS256Challenge("test-verifier"), "S256");

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_api_key_12345678");

        var result = _service.ExchangeCode(code, "test-verifier", "http://localhost:8080/callback");

        result.Should().NotBeNull();
        result!.ExpiresIn.Should().Be(2592000);
    }

    // VERIFY-3 (updated for SEC opaque tokens): access_token is an opaque token,
    // never the raw API key, and resolves back to it via the session store.
    [Fact]
    public void ExchangeCode_AccessToken_IsOpaque_AndResolvesToApiKey()
    {
        var apiKey = "ukz_test_api_key_12345678";
        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", CreateS256Challenge("test-verifier"), "S256");

        var code = _service.CompleteAuthorization(request.RequestId, apiKey);

        var result = _service.ExchangeCode(code, "test-verifier", "http://localhost:8080/callback");

        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe(apiKey, "the raw API key must never be returned as access_token");
        _sessionStore.GetApiKey(result.AccessToken).Should().Be(apiKey);
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
