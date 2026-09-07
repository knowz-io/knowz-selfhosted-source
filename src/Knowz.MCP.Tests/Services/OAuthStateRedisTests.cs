using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// Tests for MOD_OAuthStateRedis — verifies OAuth state migrated to Redis with in-memory fallback.
/// </summary>
public class OAuthStateRedisTests
{
    private readonly Mock<IDistributedCache> _cacheMock;
    private readonly Mock<ILogger<OAuthService>> _loggerMock;
    private readonly McpSessionStore _sessionStore;
    private readonly OAuthService _service;

    public OAuthStateRedisTests()
    {
        _cacheMock = new Mock<IDistributedCache>();
        _loggerMock = new Mock<ILogger<OAuthService>>();
        _sessionStore = new McpSessionStore(new Mock<ILogger<McpSessionStore>>().Object);
        _service = new OAuthService(_cacheMock.Object, _sessionStore, _loggerMock.Object);
    }

    // VERIFY-1: Authorization request stored and retrieved via Redis
    [Fact]
    public void CreateAuthorizationRequest_StoresInRedis_WithCorrectPrefix()
    {
        string? capturedKey = null;
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) => capturedKey = key);

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        capturedKey.Should().NotBeNull();
        capturedKey.Should().StartWith("oauth:req:");
    }

    // VERIFY-2: Authorization request expires after 10 minutes (Redis TTL)
    [Fact]
    public void CreateAuthorizationRequest_SetsCorrect10MinTTL()
    {
        DistributedCacheEntryOptions? capturedOptions = null;
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) => capturedOptions = options);

        _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        capturedOptions.Should().NotBeNull();
        capturedOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromMinutes(10));
    }

    // VERIFY-3: Auth code stored and retrieved via Redis
    [Fact]
    public void CompleteAuthorization_StoresAuthCode_InRedis()
    {
        // First create a request (need to capture the key to set up cache return)
        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        // Setup cache to return the stored request when asked
        _cacheMock.Setup(c => c.Get(requestKey!)).Returns(requestValue!);

        // Now capture the auth code key
        string? authCodeKey = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:code:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) => authCodeKey = key);

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");

        authCodeKey.Should().NotBeNull();
        authCodeKey.Should().StartWith("oauth:code:");
    }

    // VERIFY-4: Auth code expires after 5 minutes (Redis TTL)
    [Fact]
    public void CompleteAuthorization_SetsCorrect5MinTTL()
    {
        // Create request
        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        _cacheMock.Setup(c => c.Get(requestKey!)).Returns(requestValue!);

        DistributedCacheEntryOptions? authCodeOptions = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:code:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) => authCodeOptions = options);

        _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");

        authCodeOptions.Should().NotBeNull();
        authCodeOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromMinutes(5));
    }

    // VERIFY-5: Auth code exchange removes code from Redis (single-use)
    [Fact]
    public void ExchangeCode_RemovesAuthCode_FromRedis()
    {
        // Setup full flow: create request, complete auth, exchange code
        var verifier = "test-verifier-for-pkce";
        var challenge = CreateS256Challenge(verifier);

        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:req:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", challenge, "S256");

        _cacheMock.Setup(c => c.Get(requestKey!)).Returns(requestValue!);

        string? authCodeKey = null;
        byte[]? authCodeValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:code:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                authCodeKey = key;
                authCodeValue = value;
            });

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");

        // Setup cache to return the auth code
        _cacheMock.Setup(c => c.Get(authCodeKey!)).Returns(authCodeValue!);

        // Act
        var result = _service.ExchangeCode(code, verifier, "http://localhost:8080/callback");

        // Assert: Remove was called for the auth code key
        _cacheMock.Verify(c => c.Remove(authCodeKey!), Times.Once);
        result.Should().NotBeNull();
    }

    // VERIFY-6: CompleteAuthorization removes pending request from Redis
    [Fact]
    public void CompleteAuthorization_RemovesPendingRequest_FromRedis()
    {
        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        // Capture the request key before CompleteAuthorization overwrites it with the auth code key
        var capturedRequestKey = requestKey!;
        _cacheMock.Setup(c => c.Get(capturedRequestKey)).Returns(requestValue!);

        _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");

        _cacheMock.Verify(c => c.Remove(capturedRequestKey), Times.Once);
    }

    // VERIFY-7: Falls back to in-memory when Redis throws
    [Fact]
    public void CreateAndGet_FallsBackToInMemory_WhenRedisThrows()
    {
        // Make Redis throw on all operations
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Throws(new Exception("Redis unavailable"));
        _cacheMock.Setup(c => c.Get(It.IsAny<string>()))
            .Throws(new Exception("Redis unavailable"));

        // Create should succeed via fallback
        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        request.Should().NotBeNull();

        // Get should succeed via fallback
        var retrieved = _service.GetAuthorizationRequest(request.RequestId);
        retrieved.Should().NotBeNull();
        retrieved!.ClientId.Should().Be("test-client");
    }

    // VERIFY-7 continued: Complete auth flow works via fallback
    [Fact]
    public void CompleteAuthFlow_WorksViaFallback_WhenRedisThrows()
    {
        var verifier = "test-verifier-for-pkce";
        var challenge = CreateS256Challenge(verifier);

        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Throws(new Exception("Redis unavailable"));
        _cacheMock.Setup(c => c.Get(It.IsAny<string>()))
            .Throws(new Exception("Redis unavailable"));

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", challenge, "S256");

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");
        code.Should().NotBeNullOrEmpty();

        var result = _service.ExchangeCode(code, verifier, "http://localhost:8080/callback");
        result.Should().NotBeNull();
        result!.AccessToken.Should().NotBe("ukz_test_key_123456789");
        _sessionStore.GetApiKey(result.AccessToken).Should().Be("ukz_test_key_123456789");
    }

    // VERIFY-8: PKCE validation still works after migration
    [Fact]
    public void ExchangeCode_PkceValidation_StillWorks()
    {
        var verifier = "a-valid-code-verifier-string-1234";
        var challenge = CreateS256Challenge(verifier);

        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:req:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", challenge, "S256");

        _cacheMock.Setup(c => c.Get(requestKey!)).Returns(requestValue!);

        string? codeKey = null;
        byte[]? codeValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:code:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                codeKey = key;
                codeValue = value;
            });

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");

        // Setup code retrieval - needs to return on first get, then return null on second
        _cacheMock.Setup(c => c.Get(codeKey!)).Returns(codeValue!);

        // Valid PKCE - should succeed
        var result = _service.ExchangeCode(code, verifier, "http://localhost:8080/callback");
        result.Should().NotBeNull();
    }

    // VERIFY-8 continued: Invalid PKCE returns null
    [Fact]
    public void ExchangeCode_InvalidPkce_ReturnsNull()
    {
        var verifier = "correct-verifier-string-1234";
        var challenge = CreateS256Challenge(verifier);

        string? requestKey = null;
        byte[]? requestValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:req:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                requestKey = key;
                requestValue = value;
            });

        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", challenge, "S256");

        _cacheMock.Setup(c => c.Get(requestKey!)).Returns(requestValue!);

        string? codeKey = null;
        byte[]? codeValue = null;
        _cacheMock.Setup(c => c.Set(It.Is<string>(k => k.StartsWith("oauth:code:")), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
            {
                codeKey = key;
                codeValue = value;
            });

        var code = _service.CompleteAuthorization(request.RequestId, "ukz_test_key_123456789");
        _cacheMock.Setup(c => c.Get(codeKey!)).Returns(codeValue!);

        // Invalid PKCE - wrong verifier
        var result = _service.ExchangeCode(code, "wrong-verifier", "http://localhost:8080/callback");
        result.Should().BeNull();
    }

    // VERIFY-9: CleanupExpired cleans fallback dictionaries only
    [Fact]
    public void CleanupExpired_CleansFallbackDictionaries()
    {
        // Make Redis fail so items go to fallback
        _cacheMock.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Throws(new Exception("Redis unavailable"));
        _cacheMock.Setup(c => c.Get(It.IsAny<string>()))
            .Throws(new Exception("Redis unavailable"));

        // Create a request — it will be in fallback
        var request = _service.CreateAuthorizationRequest(
            "test-client", "http://localhost:8080/callback", "mcp:read",
            "test-state", "test-challenge", "S256");

        // Should still be accessible (not expired)
        var retrieved = _service.GetAuthorizationRequest(request.RequestId);
        retrieved.Should().NotBeNull();

        // Cleanup should not remove non-expired entries
        _service.CleanupExpired();

        retrieved = _service.GetAuthorizationRequest(request.RequestId);
        retrieved.Should().NotBeNull("non-expired entries should survive cleanup");
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
