using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.MCP.Services.Session;
using Microsoft.Extensions.Caching.Distributed;

namespace Knowz.MCP.Services;

public interface IOAuthService
{
    AuthorizationRequest CreateAuthorizationRequest(
        string clientId,
        string redirectUri,
        string scope,
        string state,
        string codeChallenge,
        string codeChallengeMethod);

    AuthorizationRequest? GetAuthorizationRequest(string requestId);

    string CompleteAuthorization(string requestId, string apiKey);

    TokenResult? ExchangeCode(string code, string codeVerifier, string redirectUri);

    void CleanupExpired();

    /// <summary>
    /// Creates a refresh token mapped to the given API key and scope.
    /// Stored in Redis with 30-day TTL.
    /// </summary>
    string CreateRefreshToken(string apiKey, string scope);

    /// <summary>
    /// Exchanges a refresh token for a new access token and rotated refresh token.
    /// The old refresh token is invalidated. Returns null if token is invalid or expired.
    /// </summary>
    TokenResult? ExchangeRefreshToken(string refreshToken);

    /// <summary>
    /// Mints an opaque, revocable access token mapped to the given API key in the
    /// MCP session store. Every OAuth grant returns one of these instead of the raw
    /// API key; McpAuthMiddleware resolves it back on subsequent requests.
    /// </summary>
    string IssueOpaqueAccessToken(string apiKey);

    /// <summary>
    /// Real lifetime (seconds) of opaque access tokens — the session store's
    /// sliding timeout. Reported as expires_in on token responses.
    /// </summary>
    int AccessTokenExpirySeconds { get; }
}

public class OAuthService : IOAuthService
{
    /// <summary>
    /// Default token expiry (30 days) — matches the session store's default sliding
    /// timeout, so it is the real lifetime of an idle opaque access token. Prefer
    /// <see cref="AccessTokenExpirySeconds"/>, which tracks the configured timeout.
    /// </summary>
    public const int TokenExpirySeconds = 2592000;

    /// <summary>
    /// Session cookie lifetime. Derived from TokenExpirySeconds to stay in sync.
    /// Used by Program.cs (initial cookie) and McpAuthMiddleware (sliding refresh).
    /// </summary>
    public static readonly TimeSpan SessionCookieMaxAge = TimeSpan.FromSeconds(TokenExpirySeconds);

    private readonly IDistributedCache _cache;
    private readonly IMcpSessionStore _sessionStore;
    private readonly ILogger<OAuthService> _logger;

    private readonly ConcurrentDictionary<string, AuthorizationRequest> _fallbackRequests = new();
    private readonly ConcurrentDictionary<string, AuthorizationCode> _fallbackCodes = new();
    private readonly ConcurrentDictionary<string, RefreshTokenData> _fallbackRefreshTokens = new();

    private const string PendingRequestPrefix = "oauth:req:";
    private const string AuthCodePrefix = "oauth:code:";
    private const string RefreshTokenPrefix = "oauth:refresh:";
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(90);

    public OAuthService(IDistributedCache cache, IMcpSessionStore sessionStore, ILogger<OAuthService> logger)
    {
        _cache = cache;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public int AccessTokenExpirySeconds => (int)_sessionStore.SessionTimeout.TotalSeconds;

    public string IssueOpaqueAccessToken(string apiKey)
    {
        var token = GenerateSecureCode();
        _sessionStore.StoreApiKey(token, apiKey);
        return token;
    }

    public AuthorizationRequest CreateAuthorizationRequest(
        string clientId,
        string redirectUri,
        string scope,
        string state,
        string codeChallenge,
        string codeChallengeMethod)
    {
        var request = new AuthorizationRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ClientId = clientId,
            RedirectUri = redirectUri,
            Scope = scope,
            State = state,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        try
        {
            var json = JsonSerializer.Serialize(request);
            _cache.SetString(PendingRequestPrefix + request.RequestId, json,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, falling back to in-memory for OAuth request {RequestId}", request.RequestId);
            _fallbackRequests[request.RequestId] = request;
        }

        _logger.LogInformation("Created authorization request {RequestId} for client {ClientId}",
            request.RequestId, clientId);
        return request;
    }

    public AuthorizationRequest? GetAuthorizationRequest(string requestId)
    {
        try
        {
            var json = _cache.GetString(PendingRequestPrefix + requestId);
            if (json != null)
            {
                var request = JsonSerializer.Deserialize<AuthorizationRequest>(json);
                if (request != null && request.ExpiresAt > DateTime.UtcNow)
                    return request;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during GetAuthorizationRequest, checking fallback");
        }

        // In-memory fallback
        if (_fallbackRequests.TryGetValue(requestId, out var fallback))
        {
            if (fallback.ExpiresAt > DateTime.UtcNow)
                return fallback;
            _fallbackRequests.TryRemove(requestId, out _);
        }

        return null;
    }

    public string CompleteAuthorization(string requestId, string apiKey)
    {
        AuthorizationRequest? request = null;

        // Try Redis first
        try
        {
            var json = _cache.GetString(PendingRequestPrefix + requestId);
            if (json != null)
            {
                request = JsonSerializer.Deserialize<AuthorizationRequest>(json);
                _cache.Remove(PendingRequestPrefix + requestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during CompleteAuthorization");
        }

        // Fallback to in-memory
        if (request == null)
        {
            if (!_fallbackRequests.TryRemove(requestId, out request))
                throw new InvalidOperationException("Authorization request not found or expired");
        }

        var code = GenerateSecureCode();
        var authCode = new AuthorizationCode
        {
            Code = code,
            ClientId = request.ClientId,
            RedirectUri = request.RedirectUri,
            Scope = request.Scope,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            ApiKey = apiKey,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };

        try
        {
            var codeJson = JsonSerializer.Serialize(authCode);
            _cache.SetString(AuthCodePrefix + code, codeJson,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, falling back to in-memory for auth code");
            _fallbackCodes[code] = authCode;
        }

        _logger.LogInformation("Generated authorization code for client {ClientId}", request.ClientId);
        return code;
    }

    public TokenResult? ExchangeCode(string code, string codeVerifier, string redirectUri)
    {
        AuthorizationCode? authCode = null;

        // Try Redis first (get + remove for single-use)
        try
        {
            var json = _cache.GetString(AuthCodePrefix + code);
            if (json != null)
            {
                authCode = JsonSerializer.Deserialize<AuthorizationCode>(json);
                _cache.Remove(AuthCodePrefix + code);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during ExchangeCode");
        }

        // Fallback to in-memory
        if (authCode == null)
        {
            if (!_fallbackCodes.TryRemove(code, out authCode))
            {
                _logger.LogWarning("Authorization code not found or already used");
                return null;
            }
        }

        if (authCode.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning("Authorization code expired");
            return null;
        }

        if (authCode.RedirectUri != redirectUri)
        {
            _logger.LogWarning("Redirect URI mismatch");
            return null;
        }

        if (!ValidatePkce(authCode.CodeChallenge, authCode.CodeChallengeMethod, codeVerifier))
        {
            _logger.LogWarning("PKCE validation failed");
            return null;
        }

        _logger.LogInformation("Successfully exchanged authorization code for client {ClientId}",
            authCode.ClientId);

        var refreshToken = CreateRefreshToken(authCode.ApiKey, authCode.Scope);

        return new TokenResult
        {
            AccessToken = IssueOpaqueAccessToken(authCode.ApiKey),
            TokenType = "Bearer",
            ExpiresIn = AccessTokenExpirySeconds,
            Scope = authCode.Scope,
            RefreshToken = refreshToken
        };
    }

    public string CreateRefreshToken(string apiKey, string scope)
    {
        var token = GenerateSecureCode();
        var data = new RefreshTokenData
        {
            ApiKey = apiKey,
            Scope = scope,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl)
        };

        try
        {
            var json = JsonSerializer.Serialize(data);
            _cache.SetString(RefreshTokenPrefix + token, json,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = RefreshTokenTtl
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, storing refresh token in memory");
            _fallbackRefreshTokens[token] = data;
        }

        _logger.LogInformation("Created refresh token for scope {Scope}", scope);
        return token;
    }

    public TokenResult? ExchangeRefreshToken(string refreshToken)
    {
        RefreshTokenData? data = null;

        // Try Redis first (get + remove for rotation)
        // DEBT-2: Non-atomic get-then-delete. Two concurrent refresh requests could both
        // succeed. Acceptable for single-client MCP sessions; use GETDEL if multi-client.
        try
        {
            var json = _cache.GetString(RefreshTokenPrefix + refreshToken);
            if (json != null)
            {
                data = JsonSerializer.Deserialize<RefreshTokenData>(json);
                _cache.Remove(RefreshTokenPrefix + refreshToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during ExchangeRefreshToken");
        }

        // Fallback to in-memory
        if (data == null)
        {
            if (!_fallbackRefreshTokens.TryRemove(refreshToken, out data))
            {
                _logger.LogWarning("Refresh token not found or already used");
                return null;
            }
        }

        if (data.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning("Refresh token expired");
            return null;
        }

        // Rotate: create a new refresh token
        var newRefreshToken = CreateRefreshToken(data.ApiKey, data.Scope);

        _logger.LogInformation("Successfully refreshed token");

        return new TokenResult
        {
            AccessToken = IssueOpaqueAccessToken(data.ApiKey),
            TokenType = "Bearer",
            ExpiresIn = AccessTokenExpirySeconds,
            Scope = data.Scope,
            RefreshToken = newRefreshToken
        };
    }

    public void CleanupExpired()
    {
        // Redis TTL handles expiration natively — only clean in-memory fallback
        var now = DateTime.UtcNow;

        foreach (var kvp in _fallbackRequests)
        {
            if (kvp.Value.ExpiresAt < now)
                _fallbackRequests.TryRemove(kvp.Key, out _);
        }

        foreach (var kvp in _fallbackCodes)
        {
            if (kvp.Value.ExpiresAt < now)
                _fallbackCodes.TryRemove(kvp.Key, out _);
        }

        foreach (var kvp in _fallbackRefreshTokens)
        {
            if (kvp.Value.ExpiresAt < now)
                _fallbackRefreshTokens.TryRemove(kvp.Key, out _);
        }
    }

    private static string GenerateSecureCode()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private static bool ValidatePkce(string codeChallenge, string method, string codeVerifier)
    {
        if (string.IsNullOrEmpty(codeVerifier))
            return false;

        if (method != "S256")
            return false;

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
        return computed == codeChallenge;
    }
}

public class AuthorizationRequest
{
    public required string RequestId { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public required string Scope { get; init; }
    public required string State { get; init; }
    public required string CodeChallenge { get; init; }
    public required string CodeChallengeMethod { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

public class AuthorizationCode
{
    public required string Code { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public required string Scope { get; init; }
    public required string CodeChallenge { get; init; }
    public required string CodeChallengeMethod { get; init; }
    public required string ApiKey { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

public class RefreshTokenData
{
    public required string ApiKey { get; init; }
    public required string Scope { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

public class TokenResult
{
    public required string AccessToken { get; init; }
    public required string TokenType { get; init; }
    public required int ExpiresIn { get; init; }
    public required string Scope { get; init; }
    public string? RefreshToken { get; init; }
}
