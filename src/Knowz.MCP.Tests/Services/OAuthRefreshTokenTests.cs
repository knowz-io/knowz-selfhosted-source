using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// Tests for FEAT_OAuthRefreshToken — verifies refresh token creation, exchange, rotation, and error handling.
/// </summary>
public class OAuthRefreshTokenTests
{
    private readonly Mock<IDistributedCache> _cacheMock;
    private readonly Mock<ILogger<OAuthService>> _loggerMock;
    private readonly McpSessionStore _sessionStore;
    private readonly OAuthService _service;

    // Track stored cache entries for retrieval
    private readonly Dictionary<string, byte[]> _cacheStore = new();

    public OAuthRefreshTokenTests()
    {
        _cacheMock = new Mock<IDistributedCache>();
        _loggerMock = new Mock<ILogger<OAuthService>>();

        // Setup cache to act like a real store
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) => _cacheStore[key] = value);

        _cacheMock.Setup(c => c.Get(It.IsAny<string>()))
            .Returns<string>(key => _cacheStore.TryGetValue(key, out var val) ? val : null);

        _cacheMock.Setup(c => c.Remove(It.IsAny<string>()))
            .Callback<string>(key => _cacheStore.Remove(key));

        _sessionStore = new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object);
        _service = new OAuthService(_cacheMock.Object, _sessionStore, _loggerMock.Object);
    }

    // VERIFY-1: authorization_code grant response includes refresh_token field
    [Fact]
    public void ExchangeCode_Returns_RefreshToken()
    {
        var verifier = "test-verifier-for-pkce-refresh";
        var challenge = CreateS256Challenge(verifier);

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", challenge, "S256");

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_api_key_12345678");

        var result = _service.ExchangeCode(code, verifier, "http://localhost:8080/callback");

        result.Should().NotBeNull();
        result!.RefreshToken.Should().NotBeNullOrEmpty("authorization_code grant must include refresh_token");
    }

    // VERIFY-3: grant_type=refresh_token returns new access_token + new refresh_token
    [Fact]
    public void ExchangeRefreshToken_ReturnsNewAccessAndRefreshTokens()
    {
        var apiKey = "ukz_test_api_key_12345678";
        var refreshToken = _service.CreateRefreshToken(apiKey, "mcp:read mcp:write");

        var result = _service.ExchangeRefreshToken(refreshToken);

        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe(apiKey, "refresh must mint an opaque token, not return the raw API key");
        _sessionStore.GetApiKey(result.AccessToken).Should().Be(apiKey);
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBe(refreshToken, "refresh token must rotate");
        result.ExpiresIn.Should().Be(OAuthService.TokenExpirySeconds);
        result.Scope.Should().Be("mcp:read mcp:write");
    }

    // VERIFY-4: Refresh token rotation: old token invalidated after use
    [Fact]
    public void ExchangeRefreshToken_InvalidatesOldToken()
    {
        var apiKey = "ukz_test_api_key_12345678";
        var refreshToken = _service.CreateRefreshToken(apiKey, "mcp:read");

        // First exchange should succeed
        var result = _service.ExchangeRefreshToken(refreshToken);
        result.Should().NotBeNull();

        // Second exchange with same token should fail
        var secondResult = _service.ExchangeRefreshToken(refreshToken);
        secondResult.Should().BeNull("old refresh token must be invalidated after use");
    }

    // VERIFY-5: Invalid refresh token returns null (would translate to invalid_grant)
    [Fact]
    public void ExchangeRefreshToken_InvalidToken_ReturnsNull()
    {
        var result = _service.ExchangeRefreshToken("non-existent-refresh-token");
        result.Should().BeNull();
    }

    // VERIFY-6: Expired refresh token returns null
    [Fact]
    public void ExchangeRefreshToken_ExpiredToken_ReturnsNull()
    {
        var apiKey = "ukz_test_api_key_12345678";

        // Create a refresh token but manually override its data to be expired
        var refreshToken = _service.CreateRefreshToken(apiKey, "mcp:read");

        // Find and replace the stored data with an expired version
        var key = _cacheStore.Keys.FirstOrDefault(k => k.StartsWith("oauth:refresh:"));
        key.Should().NotBeNull();

        var expiredData = new
        {
            ApiKey = apiKey,
            Scope = "mcp:read",
            CreatedAt = DateTime.UtcNow.AddDays(-31),
            ExpiresAt = DateTime.UtcNow.AddDays(-1) // expired yesterday
        };
        _cacheStore[key!] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expiredData));

        var result = _service.ExchangeRefreshToken(refreshToken);
        result.Should().BeNull("expired refresh token should return null");
    }

    // VERIFY-8 (updated for SEC opaque tokens): refreshed access token resolves to
    // the original API key via the session store, without exposing the key itself.
    [Fact]
    public void ExchangeRefreshToken_AccessToken_ResolvesToOriginalApiKey()
    {
        var apiKey = "ukz_original_api_key_xyz123";
        var refreshToken = _service.CreateRefreshToken(apiKey, "mcp:read");

        var result = _service.ExchangeRefreshToken(refreshToken);

        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe(apiKey);
        _sessionStore.GetApiKey(result.AccessToken).Should().Be(apiKey, "opaque access token must map back to the original API key");
    }

    // VERIFY-9: Refresh token stored in Redis with 90-day TTL
    [Fact]
    public void CreateRefreshToken_StoresWithCorrect90DayTTL()
    {
        DistributedCacheEntryOptions? capturedOptions = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:refresh:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                capturedOptions = options;
                _cacheStore[key] = value;
            });

        _service.CreateRefreshToken("ukz_test_key_123456789", "mcp:read");

        capturedOptions.Should().NotBeNull();
        capturedOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromDays(90));
    }

    // VERIFY-10: In-memory fallback works when Redis unavailable
    [Fact]
    public void RefreshTokenFlow_WorksViaFallback_WhenRedisThrows()
    {
        // Create a new service with a cache that throws
        var failingCache = new Mock<IDistributedCache>();
        failingCache.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Throws(new Exception("Redis unavailable"));
        failingCache.Setup(c => c.Get(It.IsAny<string>()))
            .Throws(new Exception("Redis unavailable"));

        var sessionStore = new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object);
        var service = new OAuthService(failingCache.Object, sessionStore, _loggerMock.Object);

        var apiKey = "ukz_test_api_key_12345678";
        var refreshToken = service.CreateRefreshToken(apiKey, "mcp:read");
        refreshToken.Should().NotBeNullOrEmpty();

        var result = service.ExchangeRefreshToken(refreshToken);
        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe(apiKey);
        sessionStore.GetApiKey(result.AccessToken).Should().Be(apiKey);
        result.RefreshToken.Should().NotBeNullOrEmpty();
    }

    // VERIFY-10 continued: rotation also works via fallback
    [Fact]
    public void RefreshTokenRotation_WorksViaFallback_WhenRedisThrows()
    {
        var failingCache = new Mock<IDistributedCache>();
        failingCache.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Throws(new Exception("Redis unavailable"));
        failingCache.Setup(c => c.Get(It.IsAny<string>()))
            .Throws(new Exception("Redis unavailable"));

        var service = new OAuthService(
            failingCache.Object,
            new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object),
            _loggerMock.Object);

        var refreshToken = service.CreateRefreshToken("ukz_test_key_123456789", "mcp:read");
        var result = service.ExchangeRefreshToken(refreshToken);
        result.Should().NotBeNull();

        // Old token should be invalidated even in fallback mode
        var secondResult = service.ExchangeRefreshToken(refreshToken);
        secondResult.Should().BeNull("old token must be invalidated even in fallback mode");
    }

    // Additional: TokenResult has RefreshToken property
    [Fact]
    public void TokenResult_HasRefreshTokenProperty()
    {
        var result = new TokenResult
        {
            AccessToken = "test",
            TokenType = "Bearer",
            ExpiresIn = 604800,
            Scope = "mcp:read",
            RefreshToken = "test-refresh-token"
        };

        result.RefreshToken.Should().Be("test-refresh-token");
    }

    // Additional: TokenResult RefreshToken is nullable
    [Fact]
    public void TokenResult_RefreshToken_IsNullable()
    {
        var result = new TokenResult
        {
            AccessToken = "test",
            TokenType = "Bearer",
            ExpiresIn = 604800,
            Scope = "mcp:read"
        };

        result.RefreshToken.Should().BeNull();
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
