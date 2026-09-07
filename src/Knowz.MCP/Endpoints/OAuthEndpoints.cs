using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Knowz.MCP.Helpers;
using Knowz.MCP.Services;

namespace Knowz.MCP.Endpoints;

public static class OAuthEndpoints
{
    public static WebApplication MapOAuthEndpoints(this WebApplication app)
    {
        // RFC 8414: OAuth 2.0 Authorization Server Metadata
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext context) =>
        {
            var baseUrl = ApiKeyValidator.GetBaseUrl(context);
            return Results.Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = $"{baseUrl}/oauth/authorize",
                token_endpoint = $"{baseUrl}/oauth/token",
                registration_endpoint = $"{baseUrl}/oauth/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic", "none" },
                scopes_supported = new[] { "mcp:read", "mcp:write" }
            });
        });

        // RFC 9728: OAuth 2.0 Protected Resource Metadata.
        // The advertised resource identifier has a path component ("{baseUrl}/mcp"), so RFC 9728
        // requires a compliant client to insert the well-known segment BEFORE that path:
        //   {baseUrl}/.well-known/oauth-protected-resource/mcp
        // Both forms are served directly and identically (never a redirect): the path-inserted
        // form for compliant clients, the root form as a compatibility alias for clients that
        // discovered via the historical WWW-Authenticate value or probe the bare well-known path.
        // Single builder so the two documents cannot drift.
        var buildProtectedResourceMetadata = (string baseUrl) => new
        {
            resource = $"{baseUrl}/mcp",
            authorization_servers = new[] { baseUrl },
            scopes_supported = new[] { "mcp:read", "mcp:write" },
            bearer_methods_supported = new[] { "header" },
            resource_documentation = $"{baseUrl}/docs"
        };

        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) =>
            Results.Json(buildProtectedResourceMetadata(ApiKeyValidator.GetBaseUrl(context))));

        app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext context) =>
            Results.Json(buildProtectedResourceMetadata(ApiKeyValidator.GetBaseUrl(context))));

        // OpenID Connect Discovery fallback
        app.MapGet("/.well-known/openid-configuration", (HttpContext context) =>
        {
            var baseUrl = ApiKeyValidator.GetBaseUrl(context);
            return Results.Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = $"{baseUrl}/oauth/authorize",
                token_endpoint = $"{baseUrl}/oauth/token",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
                scopes_supported = new[] { "mcp:read", "mcp:write" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }
            });
        });

        // OAuth2 Token Endpoint
        app.MapPost("/oauth/token", async (HttpContext context, IOAuthService oauthService) =>
        {
            var contentType = context.Request.ContentType ?? "";

            if (!contentType.Contains("application/x-www-form-urlencoded"))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "Content-Type must be application/x-www-form-urlencoded"
                });
            }

            var form = await context.Request.ReadFormAsync();
            var grantType = form["grant_type"].FirstOrDefault();

            if (grantType == "authorization_code")
            {
                var code = form["code"].FirstOrDefault();
                var redirectUri = form["redirect_uri"].FirstOrDefault();
                var codeVerifier = form["code_verifier"].FirstOrDefault();

                if (string.IsNullOrEmpty(code))
                    return Results.BadRequest(new { error = "invalid_request", error_description = "Missing code" });

                if (string.IsNullOrEmpty(redirectUri))
                    return Results.BadRequest(new { error = "invalid_request", error_description = "Missing redirect_uri" });

                if (string.IsNullOrEmpty(codeVerifier))
                    return Results.BadRequest(new { error = "invalid_request", error_description = "Missing code_verifier for PKCE" });

                var tokenResult = oauthService.ExchangeCode(code, codeVerifier, redirectUri);
                if (tokenResult == null)
                    return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid or expired authorization code" });

                return Results.Json(new
                {
                    access_token = tokenResult.AccessToken,
                    token_type = tokenResult.TokenType,
                    expires_in = tokenResult.ExpiresIn,
                    scope = tokenResult.Scope,
                    refresh_token = tokenResult.RefreshToken
                });
            }

            if (grantType == "client_credentials")
            {
                string? clientId = form["client_id"].FirstOrDefault();
                string? clientSecret = form["client_secret"].FirstOrDefault();

                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var encodedCredentials = authHeader.Substring(6).Trim();
                            var decodedBytes = Convert.FromBase64String(encodedCredentials);
                            var decodedCredentials = Encoding.UTF8.GetString(decodedBytes);
                            var parts = decodedCredentials.Split(':', 2);

                            if (parts.Length == 2)
                            {
                                clientId = parts[0];
                                clientSecret = parts[1];
                            }
                        }
                        catch (FormatException)
                        {
                            return Results.BadRequest(new
                            {
                                error = "invalid_request",
                                error_description = "Invalid Basic authentication format"
                            });
                        }
                    }
                    else if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        clientSecret = authHeader.Substring(7).Trim();
                        clientId = clientId ?? "public-client";
                    }
                }

                if (string.IsNullOrWhiteSpace(clientSecret))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "Missing client_secret"
                    });
                }

                if (!ApiKeyValidator.IsValidApiKey(clientSecret))
                {
                    return Results.Unauthorized();
                }

                // Issue an opaque session-store-backed token instead of returning the raw
                // API key. McpAuthMiddleware maps it back on subsequent requests.
                var opaqueToken = oauthService.IssueOpaqueAccessToken(clientSecret);

                var refreshToken = oauthService.CreateRefreshToken(clientSecret, "mcp:read mcp:write");

                return Results.Json(new
                {
                    access_token = opaqueToken,
                    token_type = "Bearer",
                    expires_in = oauthService.AccessTokenExpirySeconds,
                    scope = "mcp:read mcp:write",
                    refresh_token = refreshToken
                });
            }

            if (grantType == "refresh_token")
            {
                var refreshToken = form["refresh_token"].FirstOrDefault();

                if (string.IsNullOrEmpty(refreshToken))
                    return Results.BadRequest(new { error = "invalid_request", error_description = "Missing refresh_token" });

                var tokenResult = oauthService.ExchangeRefreshToken(refreshToken);
                if (tokenResult == null)
                    return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid or expired refresh token" });

                return Results.Json(new
                {
                    access_token = tokenResult.AccessToken,
                    token_type = tokenResult.TokenType,
                    expires_in = tokenResult.ExpiresIn,
                    scope = tokenResult.Scope,
                    refresh_token = tokenResult.RefreshToken
                });
            }

            return Results.BadRequest(new
            {
                error = "unsupported_grant_type",
                error_description = "Supported grant types: authorization_code, client_credentials, refresh_token"
            });
        });

        // OAuth2 Authorization Endpoint - Browser-based flow
        app.MapGet("/oauth/authorize", async (HttpContext context, IOAuthService oauthService, IMcpSSOService mcpSSOService) =>
        {
            var responseType = context.Request.Query["response_type"].FirstOrDefault();
            var clientId = context.Request.Query["client_id"].FirstOrDefault();
            var redirectUri = context.Request.Query["redirect_uri"].FirstOrDefault();
            var scope = context.Request.Query["scope"].FirstOrDefault() ?? "mcp:read mcp:write";
            var state = context.Request.Query["state"].FirstOrDefault() ?? "";
            var codeChallenge = context.Request.Query["code_challenge"].FirstOrDefault();
            var codeChallengeMethod = context.Request.Query["code_challenge_method"].FirstOrDefault() ?? "S256";

            if (responseType != "code")
                return Results.BadRequest(new { error = "unsupported_response_type", error_description = "Only 'code' response type is supported" });

            if (string.IsNullOrEmpty(clientId))
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing client_id" });

            if (string.IsNullOrEmpty(redirectUri))
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing redirect_uri" });

            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var hostedCallbackPolicy = context.RequestServices.GetRequiredService<HostedConnectorCallbackPolicy>();

            if (!IsAllowedRedirectUri(redirectUri, hostedCallbackPolicy))
                return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri must be localhost, a valid native app scheme, or a registered hosted connector callback" });

            if (string.IsNullOrEmpty(codeChallenge))
                return Results.BadRequest(new { error = "invalid_request", error_description = "PKCE code_challenge is required" });

            var authRequest = oauthService.CreateAuthorizationRequest(
                clientId, redirectUri, scope, state, codeChallenge, codeChallengeMethod);

            var baseUrl = ApiKeyValidator.GetBaseUrl(context);
            var authPageOptions = await ResolveAuthPageOptionsAsync(mcpSSOService);
            var apiKeyHelpUrl = configuration["MCP:ApiKeyHelpUrl"];
            var backendMode = configuration["MCP:BackendMode"] ?? "proxy";
            var html = OAuthPageGenerator.GenerateLoginPage(authRequest.RequestId, baseUrl, scope,
                ssoProviders: authPageOptions.SsoProviders,
                apiKeyHelpUrl: apiKeyHelpUrl,
                backendMode: backendMode,
                authPageOptions: authPageOptions);

            return Results.Content(html, "text/html");
        });

        // Handle login form submission (API key or username/password)
        app.MapPost("/oauth/authorize", async (HttpContext context, IOAuthService oauthService, IMcpSSOService mcpSSOService, IHttpClientFactory httpClientFactory) =>
        {
            var form = await context.Request.ReadFormAsync();
            var requestId = form["request_id"].FirstOrDefault();
            var apiKey = form["api_key"].FirstOrDefault();
            var email = form["email"].FirstOrDefault();
            var password = form["password"].FirstOrDefault();

            if (string.IsNullOrEmpty(requestId))
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing request_id" });

            var authRequest = oauthService.GetAuthorizationRequest(requestId);
            if (authRequest == null)
                return Results.BadRequest(new { error = "invalid_request", error_description = "Authorization request expired or not found" });

            var authPageOptions = await ResolveAuthPageOptionsAsync(mcpSSOService);
            var ssoProviders = authPageOptions.SsoProviders;
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var apiKeyHelpUrl = configuration["MCP:ApiKeyHelpUrl"];
            var backendMode = configuration["MCP:BackendMode"] ?? "proxy";
            var apiBaseUrl = configuration["Knowz:BaseUrl"]
                ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured");
            var client = httpClientFactory.CreateClient("McpApiClient");

            // Helper to render login page with error
            IResult RenderError(string error)
            {
                var baseUrl = ApiKeyValidator.GetBaseUrl(context);
                var errorHtml = OAuthPageGenerator.GenerateLoginPage(requestId, baseUrl, authRequest.Scope,
                    error,
                    ssoProviders: ssoProviders,
                    apiKeyHelpUrl: apiKeyHelpUrl,
                    backendMode: backendMode,
                    authPageOptions: authPageOptions);
                return Results.Content(errorHtml, "text/html");
            }

            // Username/password authentication (all modes — platform and self-hosted)
            if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password))
            {
                if (!authPageOptions.ShowPasswordLogin)
                    return RenderError("Organization SSO is required for username/password sign-in. Use SSO or an API key.");

                var serviceKey = configuration["MCP:ServiceKey"];
                if (string.IsNullOrEmpty(serviceKey))
                    return RenderError("Server configuration error. MCP:ServiceKey is not configured.");

                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}/api/v1/internal/mcp/authenticate")
                    {
                        Content = JsonContent.Create(new { email, password })
                    };
                    request.Headers.Add("X-Service-Key", serviceKey);

                    var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                        return RenderError("Invalid username or password.");

                    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (body.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("apiKey", out var apiKeyEl))
                    {
                        apiKey = apiKeyEl.GetString();
                    }

                    if (string.IsNullOrEmpty(apiKey))
                        return RenderError("Authentication succeeded but no API key was returned.");
                }
                catch (Exception)
                {
                    return RenderError("Unable to connect to the authentication service. Please try again.");
                }
            }
            // API key authentication
            else if (!string.IsNullOrEmpty(apiKey))
            {
                if (!ApiKeyValidator.IsValidApiKey(apiKey))
                    return RenderError("Invalid API key format. Keys must start with kz_, ukz_, or ksh_ and be at least 20 characters.");

                // Validate API key by calling the backend API
                var validationEndpoint = configuration["MCP:ApiKeyValidationEndpoint"]
                    ?? (backendMode.Equals("selfhosted", StringComparison.OrdinalIgnoreCase)
                        ? "/api/v1/auth/me"
                        : "/api/v1/auth/validate-key");

                try
                {
                    HttpResponseMessage response;

                    if (validationEndpoint == "/api/v1/auth/validate-key")
                    {
                        // Platform mode: use dedicated validation endpoint (POST with body)
                        // This endpoint checks all key sources including TenantApiKeys table
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}{validationEndpoint}");
                        request.Content = JsonContent.Create(new { apiKey });
                        response = await client.SendAsync(request);
                    }
                    else
                    {
                        // Self-hosted or custom endpoint: use GET with X-Api-Key header (existing behavior)
                        var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}{validationEndpoint}");
                        request.Headers.Add("X-Api-Key", apiKey);
                        response = await client.SendAsync(request);
                    }

                    if (!response.IsSuccessStatusCode)
                        return RenderError("Invalid or expired API key. Please check your key and try again.");
                }
                catch (Exception)
                {
                    return RenderError("Unable to validate API key. Please try again.");
                }
            }
            else
            {
                return RenderError("Please enter your credentials or API key.");
            }

            // CompleteAuthorization removes the request from Redis/in-memory atomically.
            // A double-submit or stale browser tab can race past the GetAuthorizationRequest
            // check above and surface as InvalidOperationException here — render the
            // friendly error page rather than a 500, matching the SSO-callback handler below.
            string code;
            try
            {
                code = oauthService.CompleteAuthorization(requestId, apiKey!);
            }
            catch (InvalidOperationException)
            {
                var baseUrl = ApiKeyValidator.GetBaseUrl(context);
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, "Authorization request expired. Please try again.");
                return Results.Content(errorHtml, "text/html");
            }

            var redirectUrl = $"{authRequest.RedirectUri}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(authRequest.State)}";

            return Results.Redirect(redirectUrl);
        });

        // RFC 7591: OAuth 2.0 Dynamic Client Registration
        app.MapPost("/oauth/register", async (HttpContext context) =>
        {
            try
            {
                var registration = await context.Request.ReadFromJsonAsync<JsonElement>();

                var clientName = registration.TryGetProperty("client_name", out var nameEl)
                    ? nameEl.GetString() ?? "unknown-client"
                    : "unknown-client";

                var redirectUris = registration.TryGetProperty("redirect_uris", out var urisEl)
                    ? urisEl.EnumerateArray().Select(u => u.GetString()).ToArray()
                    : Array.Empty<string?>();

                var clientId = $"mcp-{Guid.NewGuid():N}".Substring(0, 32);
                var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                return Results.Json(new
                {
                    client_id = clientId,
                    client_name = clientName,
                    redirect_uris = redirectUris,
                    grant_types = new[] { "authorization_code" },
                    response_types = new[] { "code" },
                    token_endpoint_auth_method = "none",
                    client_id_issued_at = issuedAt,
                    client_secret_expires_at = 0
                });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_client_metadata",
                    error_description = "Invalid JSON in registration request"
                });
            }
        });

        // SSO initiation - redirects to OIDC provider
        app.MapGet("/oauth/sso/start", async (HttpContext context, IOAuthService oauthService, IMcpSSOService mcpSSOService) =>
        {
            var provider = context.Request.Query["provider"].FirstOrDefault();
            var requestId = context.Request.Query["requestId"].FirstOrDefault();

            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(requestId))
                return Results.BadRequest(new { error = "Missing provider or requestId" });

            // Verify the MCP OAuth request exists
            var authRequest = oauthService.GetAuthorizationRequest(requestId);
            if (authRequest == null)
                return Results.BadRequest(new { error = "Authorization request not found or expired" });

            var baseUrl = ApiKeyValidator.GetBaseUrl(context);
            var callbackUrl = $"{baseUrl}/oauth/sso/callback";

            var result = await mcpSSOService.StartSSOFlowAsync(provider, requestId, callbackUrl);
            if (!result.Success)
                return Results.BadRequest(new { error = result.ErrorMessage });

            return Results.Redirect(result.AuthorizationUrl!);
        });

        // SSO callback - receives OIDC callback, resolves to API key, completes MCP OAuth flow
        app.MapGet("/oauth/sso/callback", async (HttpContext context, IOAuthService oauthService, IMcpSSOService mcpSSOService) =>
        {
            var code = context.Request.Query["code"].FirstOrDefault();
            var state = context.Request.Query["state"].FirstOrDefault();
            var error = context.Request.Query["error"].FirstOrDefault();

            var baseUrl = ApiKeyValidator.GetBaseUrl(context);

            // Handle OIDC error responses (user cancelled, etc.)
            if (!string.IsNullOrEmpty(error))
            {
                var errorDescription = context.Request.Query["error_description"].FirstOrDefault()
                    ?? "Authentication was cancelled.";
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, errorDescription);
                return Results.Content(errorHtml, "text/html");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, "Missing authorization code or state parameter.");
                return Results.Content(errorHtml, "text/html");
            }

            var result = await mcpSSOService.HandleSSOCallbackAsync(code, state);
            if (!result.Success)
            {
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, result.ErrorMessage ?? "SSO authentication failed.");
                return Results.Content(errorHtml, "text/html");
            }

            // Get the MCP OAuth request BEFORE completing (CompleteAuthorization removes it)
            var authRequest = oauthService.GetAuthorizationRequest(result.RequestId!);
            if (authRequest == null)
            {
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, "Authorization request expired. Please try again.");
                return Results.Content(errorHtml, "text/html");
            }

            // Complete the MCP OAuth authorization request with the resolved API key
            string authCode;
            try
            {
                authCode = oauthService.CompleteAuthorization(result.RequestId!, result.ApiKey!);
            }
            catch (InvalidOperationException)
            {
                var errorHtml = OAuthPageGenerator.GenerateErrorPage(baseUrl, "Authorization request expired. Please try again.");
                return Results.Content(errorHtml, "text/html");
            }

            var redirectUrl = $"{authRequest.RedirectUri}?code={Uri.EscapeDataString(authCode)}&state={Uri.EscapeDataString(authRequest.State)}";
            return Results.Redirect(redirectUrl);
        });

        return app;
    }

    /// <summary>
    /// Gets page policy from the SSO service while tolerating older test mocks
    /// that only set up GetEnabledProviders.
    /// </summary>
    private static async Task<McpAuthPageOptions> ResolveAuthPageOptionsAsync(IMcpSSOService mcpSSOService)
    {
        var optionsTask = mcpSSOService.GetAuthPageOptionsAsync();
        var options = optionsTask is null ? null : await optionsTask;
        return options ?? new McpAuthPageOptions
        {
            SsoProviders = mcpSSOService.GetEnabledProviders()
        };
    }

    /// <summary>
    /// Validates redirect URIs per RFC 8252: allows loopback redirects (§7.3)
    /// and private-use URI schemes for native apps (§7.1).
    /// PKCE (required on this endpoint) protects against interception regardless of redirect method.
    ///
    /// Provider-hosted HTTPS callbacks (Claude, ChatGPT, Cursor cloud agents, ...) are matched by
    /// <see cref="HostedConnectorCallbackPolicy"/>, whose allowlist is configurable — see
    /// <see cref="HostedConnectorCallbackPolicy.ConfigurationKey"/>. Onboarding a new hosted
    /// connector is a config change, not a code change.
    ///
    /// The policy is passed in, not resolved here, and the caller resolves it with
    /// GetRequiredService: there is exactly ONE way this allowlist gets built. An earlier draft
    /// fell back to building a policy straight from configuration when nothing was registered,
    /// which meant production ran the registered singleton while every test ran the fallback —
    /// a missing registration must fail fast, not silently diverge.
    /// </summary>
    private static bool IsAllowedRedirectUri(
        string redirectUri,
        HostedConnectorCallbackPolicy hostedCallbackPolicy)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;

        // Hosted web connectors use provider-owned HTTPS callbacks, matched against the allowlist
        // parsed and logged once at startup.
        if (hostedCallbackPolicy.IsAllowed(uri))
            return true;

        // Loopback redirects (RFC 8252 §7.3)
        if (uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Private-use URI schemes for native apps (RFC 8252 §7.1)
        // e.g. cursor://mcp/oauth/callback, warp://mcp/oauth2callback, vscode://..., etc.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return true;

        return false;
    }
}
