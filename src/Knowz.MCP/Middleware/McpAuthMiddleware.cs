using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;

namespace Knowz.MCP.Middleware;

public class McpAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpAuthMiddleware> _logger;
    private readonly IConfiguration _configuration;

    public McpAuthMiddleware(
        RequestDelegate next,
        ILogger<McpAuthMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, IMcpSessionStore sessionStore)
    {
        // Skip auth for public endpoints
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/healthz") ||
            context.Request.Path.StartsWithSegments("/.well-known"))
        {
            await _next(context);
            return;
        }

        // OAuth endpoints pass through
        if (context.Request.Path.StartsWithSegments("/oauth"))
        {
            await _next(context);
            return;
        }

        await HandleProxyModeAuth(context, sessionStore);
    }

    /// <summary>
    /// Proxy mode auth: extract API key and validate format.
    /// </summary>
    private async Task HandleProxyModeAuth(HttpContext context, IMcpSessionStore sessionStore)
    {
        // MCP Streamable HTTP endpoint - require auth per MCP spec (RFC 9728)
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            ExtractApiKeyForProxy(context, sessionStore);

            // If no valid API key was resolved, return 401 with WWW-Authenticate
            // to trigger the MCP OAuth flow in compliant clients (Claude Code, mcp-remote, etc.)
            if (context.Items["ApiKey"] is not string)
            {
                var mcpSessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
                var cookieSession = context.Request.Cookies["mcp_session"];
                var hasAuthHeader = !string.IsNullOrEmpty(context.Request.Headers["Authorization"].FirstOrDefault());

                // Determine the most likely cause to help users self-diagnose
                string diagnosticHint;
                if (!string.IsNullOrEmpty(mcpSessionId) || !string.IsNullOrEmpty(cookieSession))
                {
                    // Had a session but API key not found — session expired or server restarted
                    diagnosticHint = "MCP session was found but the associated API key has expired or was lost (server restart). " +
                                     "Re-authenticate by running the MCP login flow again.";
                    _logger.LogWarning(
                        "Auth failed on /mcp: session found (header={SessionHeader}, cookie={SessionCookie}) but no API key in store. " +
                        "Likely cause: server restart or session expiry.",
                        mcpSessionId ?? "(none)", cookieSession ?? "(none)");
                }
                else if (hasAuthHeader)
                {
                    // Sent an auth header but it wasn't a valid Knowz key format
                    diagnosticHint = "Authorization header was provided but did not contain a valid Knowz API key. " +
                                     "Ensure the Bearer token is a valid API key (starting with kz_, ukz_, or ksh_).";
                    _logger.LogWarning("Auth failed on /mcp: Authorization header present but no valid Knowz API key extracted.");
                }
                else
                {
                    // No session, no auth header — first-time or client not sending credentials
                    diagnosticHint = "No authentication credentials found. Complete the MCP OAuth login flow first.";
                    _logger.LogWarning("Auth failed on /mcp: no session ID, no auth header, no fallback available.");
                }

                var baseUrl = Helpers.ApiKeyValidator.GetBaseUrl(context);
                context.Response.StatusCode = 401;
                // RFC 9728: the metadata URL for a resource identifier with a path component
                // ("{baseUrl}/mcp") inserts the well-known segment before that path. The root
                // form is still served as a compatibility alias, but new clients converge here.
                context.Response.Headers["WWW-Authenticate"] =
                    $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource/mcp\"";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "unauthorized",
                    error_description = diagnosticHint
                });
                return;
            }

            await _next(context);
            return;
        }

        // Legacy SSE transport
        if (context.Request.Path.StartsWithSegments("/sse") ||
            context.Request.Path.StartsWithSegments("/message"))
        {
            ExtractApiKeyForProxy(context, sessionStore);
            await _next(context);
            return;
        }

        var validateApiKey = _configuration.GetValue<bool>("Authentication:ValidateApiKey", true);

        if (!validateApiKey)
        {
            _logger.LogWarning("API key validation is disabled");
            await _next(context);
            return;
        }

        var apiKey = ResolveOpaqueAccessToken(ExtractApiKeyFromRequest(context), sessionStore);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Request missing API key");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_token",
                error_description = "Missing or invalid access token. Include Authorization: Bearer <api-key> header."
            });
            return;
        }

        if (!IsValidKnowzKeyFormat(apiKey))
        {
            _logger.LogWarning("Invalid API key format");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_token",
                error_description = "Invalid API key format. Keys must start with kz_ or ukz_."
            });
            return;
        }

        context.Items["ApiKey"] = apiKey;
        await _next(context);
    }

    // --- Helper methods ---

    /// <summary>
    /// Extracts API key from request headers.
    /// Supports: X-Api-Key header, Authorization: Bearer header.
    /// </summary>
    internal static string? ExtractApiKeyFromRequest(HttpContext context)
    {
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = authHeader.Substring(7).Trim();
            }
        }

        return apiKey;
    }

    /// <summary>
    /// Extracts vault scoping parameters from query string.
    /// </summary>
    private void ExtractVaultScoping(HttpContext context)
    {
        var vaultId = context.Request.Query["vaultId"].FirstOrDefault();
        var defaultVaultId = context.Request.Query["defaultVaultId"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(vaultId))
        {
            context.Items["SandboxVaultId"] = vaultId;
            _logger.LogInformation("MCP session sandboxed to vault: {VaultId}", vaultId);
        }

        if (!string.IsNullOrWhiteSpace(defaultVaultId))
        {
            context.Items["DefaultVaultId"] = defaultVaultId;
            _logger.LogInformation("MCP session default vault: {VaultId}", defaultVaultId);
        }
    }

    /// <summary>
    /// Opaque OAuth access tokens minted by /oauth/token are session-store keys
    /// mapping to the underlying API key. Any Bearer credential that isn't a raw
    /// Knowz key is looked up in the store; unknown values pass through unchanged
    /// so they fail the downstream format check and get a 401.
    /// </summary>
    private string? ResolveOpaqueAccessToken(string? credential, IMcpSessionStore sessionStore)
    {
        if (string.IsNullOrWhiteSpace(credential) || IsValidKnowzKeyFormat(credential))
            return credential;

        var resolved = sessionStore.GetApiKey(credential);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            _logger.LogInformation("Resolved opaque OAuth access token to stored API key");
            return resolved;
        }

        return credential;
    }

    private static bool IsValidKnowzKeyFormat(string apiKey)
    {
        return (apiKey.StartsWith("kz_") || apiKey.StartsWith("ukz_") || apiKey.StartsWith("ksh_") || apiKey.StartsWith("sh-")) && apiKey.Length >= 20;
    }

    /// <summary>
    /// Proxy mode: extract API key and store in session store.
    /// </summary>
    private void ExtractApiKeyForProxy(HttpContext context, IMcpSessionStore sessionStore)
    {
        // Log all authorization-related headers for debugging
        var authHeaderPreview = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeaderPreview) && authHeaderPreview.Length > 20)
            authHeaderPreview = authHeaderPreview.Substring(0, 20) + "...";

        var xApiKeyRaw = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        var xApiKeyMasked = xApiKeyRaw is not null && xApiKeyRaw.Length > 4
            ? xApiKeyRaw[..4] + "****"
            : xApiKeyRaw ?? "(none)";

        _logger.LogInformation("ExtractApiKey: Path={Path}, X-Api-Key={XApiKey}, Authorization={Auth}, Mcp-Session-Id={SessionId}",
            context.Request.Path,
            xApiKeyMasked,
            authHeaderPreview ?? "(none)",
            context.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? "(none)");

        var apiKey = ResolveOpaqueAccessToken(ExtractApiKeyFromRequest(context), sessionStore);
        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

        // Cookie fallback: some MCP clients (including Claude Code) don't send the
        // Mcp-Session-Id header on subsequent requests after the initial handshake.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = context.Request.Cookies["mcp_session"];
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _logger.LogInformation("Using session from cookie fallback: {SessionId}", sessionId);
            }
        }

        // If no API key in request, try session store
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(sessionId))
        {
            apiKey = sessionStore.GetApiKey(sessionId);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("Retrieved API key from session store for session {SessionId}", sessionId);
            }
            else
            {
                _logger.LogWarning("No API key found in session store for session {SessionId}", sessionId);
            }
        }

        if (!string.IsNullOrWhiteSpace(apiKey) && IsValidKnowzKeyFormat(apiKey))
        {
            context.Items["ApiKey"] = apiKey;
            _logger.LogInformation("API key set in HttpContext.Items for path {Path}", context.Request.Path);

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                sessionStore.StoreApiKey(sessionId, apiKey);

                // Sliding cookie extension: refresh the session cookie on every authenticated request
                // so active clients never have their cookies expire mid-use
                context.Response.Cookies.Append("mcp_session", sessionId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    MaxAge = OAuthService.SessionCookieMaxAge
                });

                _logger.LogInformation("Stored API key in session store for session {SessionId}", sessionId);
            }
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("API key found but invalid format: {Prefix}...", apiKey.Substring(0, Math.Min(5, apiKey.Length)));
        }
        else
        {
            _logger.LogWarning("No API key found in request for path {Path}", context.Request.Path);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            context.Items["McpSessionId"] = sessionId;
        }

        // Deployment resilience: if the client sends a Mcp-Session-Id that the SDK
        // doesn't recognize (container restarted), strip it so the SDK creates a new
        // session instead of returning 404. Auth is preserved via Redis-backed session store.
        if (context.Items["ApiKey"] is string && !string.IsNullOrWhiteSpace(sessionId))
        {
            var tracker = context.RequestServices.GetService<IActiveSessionTracker>();
            if (tracker != null && !tracker.IsKnown(sessionId))
            {
                context.Request.Headers.Remove("Mcp-Session-Id");
                _logger.LogInformation(
                    "Stripped stale Mcp-Session-Id {SessionId} after deployment — forcing transparent session re-creation",
                    sessionId);
            }
        }

        // Vault scoping
        ExtractVaultScoping(context);
    }
}

public static class McpAuthExtensions
{
    public static string? GetApiKey(this HttpContext context)
        => context.Items["ApiKey"] as string;

    public static Guid? GetTenantId(this HttpContext context)
        => context.Items["TenantId"] as Guid?;

    public static string? GetSandboxVaultId(this HttpContext context)
        => context.Items["SandboxVaultId"] as string;

    public static string? GetDefaultVaultId(this HttpContext context)
        => context.Items["DefaultVaultId"] as string;

    public static string? ResolveVaultId(this HttpContext context, string? toolVaultId)
    {
        var sandboxVaultId = context.GetSandboxVaultId();
        if (!string.IsNullOrWhiteSpace(sandboxVaultId))
            return sandboxVaultId;
        if (!string.IsNullOrWhiteSpace(toolVaultId))
            return toolVaultId;
        return context.GetDefaultVaultId();
    }

    public static bool IsSandboxed(this HttpContext context)
        => !string.IsNullOrWhiteSpace(context.GetSandboxVaultId());
}
