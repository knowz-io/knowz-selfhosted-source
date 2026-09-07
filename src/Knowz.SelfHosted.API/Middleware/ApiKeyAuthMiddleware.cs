using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.API.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SelfHostedOptions _options;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    private static readonly string[] PublicPathPrefixes = ["/healthz", "/swagger", "/index.html"];

    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        IOptions<SelfHostedOptions> options,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Always allow public paths and static files
        if (IsPublicPath(path) || IsStaticFile(path))
        {
            await _next(context);
            return;
        }

        // Allow GET requests for non-API paths (SPA routes that fall back to index.html)
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // If no API key is configured, allow all requests
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            await _next(context);
            return;
        }

        // Require API key for API endpoints
        var providedKey = ApiKeyExtractor.Extract(context);
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Missing API key. Include X-Api-Key header or Authorization: Bearer header."
            });
            return;
        }

        var configuredBytes = Encoding.UTF8.GetBytes(_options.ApiKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        
        // FixedTimeEquals requires equal-length arrays; check length first to avoid exception
        if (configuredBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes))
        {
            _logger.LogWarning("Invalid API key provided from {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
            return;
        }

        await _next(context);
    }

    private static bool IsPublicPath(string path) =>
        PublicPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsStaticFile(string path) =>
        path.Contains('.') && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
