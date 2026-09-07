using Knowz.Core.Configuration;
using Knowz.SelfHosted.API.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests that SSO endpoints are accessible without authentication via the middleware.
/// </summary>
public class SSOAuthMiddlewareTests
{
    private readonly SelfHostedOptions _options;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public SSOAuthMiddlewareTests()
    {
        _options = new SelfHostedOptions
        {
            JwtSecret = "this-is-a-test-secret-key-at-least-32-characters",
            JwtIssuer = "test-issuer",
            ApiKey = "test-api-key"
        };
        _logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
    }

    [Theory]
    [InlineData("/api/v1/auth/sso/providers")]
    [InlineData("/api/v1/auth/sso/authorize")]
    [InlineData("/api/v1/auth/sso/callback")]
    [InlineData("/api/v1/auth/sso/providers?something=1")]
    public async Task SSOEndpoints_AreAccessible_WithoutAuthentication(string path)
    {
        var nextCalled = false;
        var middleware = new AuthenticationMiddleware(
            next: async (ctx) => { nextCalled = true; await Task.CompletedTask; },
            Options.Create(_options),
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path.Split('?')[0]; // Strip query for Path
        if (path.Contains('?'))
            httpContext.Request.QueryString = new QueryString("?" + path.Split('?')[1]);
        httpContext.Request.Method = "GET";

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextCalled, $"Request to {path} should pass through middleware without auth");
        Assert.NotEqual(401, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task SSOCallback_POST_IsAccessible_WithoutAuthentication()
    {
        var nextCalled = false;
        var middleware = new AuthenticationMiddleware(
            next: async (ctx) => { nextCalled = true; await Task.CompletedTask; },
            Options.Create(_options),
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/auth/sso/callback";
        httpContext.Request.Method = "POST";

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextCalled, "POST to /api/v1/auth/sso/callback should pass through without auth");
    }

    [Fact]
    public async Task LoginEndpoint_StillAccessible_WithoutAuthentication()
    {
        // Regression test: existing login endpoint still works
        var nextCalled = false;
        var middleware = new AuthenticationMiddleware(
            next: async (ctx) => { nextCalled = true; await Task.CompletedTask; },
            Options.Create(_options),
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/auth/login";
        httpContext.Request.Method = "POST";

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextCalled, "POST to /api/v1/auth/login should pass through without auth");
    }

    [Fact]
    public async Task ProtectedEndpoint_StillRequiresAuth()
    {
        var nextCalled = false;
        var middleware = new AuthenticationMiddleware(
            next: async (ctx) => { nextCalled = true; await Task.CompletedTask; },
            Options.Create(_options),
            _logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/knowledge";
        httpContext.Request.Method = "GET";

        // Prevent writing to response body to avoid test issues
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.False(nextCalled, "GET to /api/v1/knowledge should NOT pass through without auth");
        Assert.Equal(401, httpContext.Response.StatusCode);
    }
}
