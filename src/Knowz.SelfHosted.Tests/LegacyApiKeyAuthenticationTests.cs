using System.Security.Claims;
using Knowz.Core.Configuration;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.API.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// SEC_P0Triage Item 8 (§Rule 7): the legacy global API key
/// (<c>SelfHosted:ApiKey</c>) authenticates as role <c>LegacyApiKey</c> —
/// never SuperAdmin. Caller reaches knowledge/search/vault endpoints but
/// is refused at /api/superadmin/*, /api/config/*, /api/users/*,
/// /api/admin/* via <see cref="AuthorizationHelpers.IsSuperAdmin"/>.
/// </summary>
public class LegacyApiKeyAuthenticationTests
{
    private const string ConfiguredKey = "legacy-test-key-opaque-value-1234";

    private static AuthenticationMiddleware BuildMiddleware(ILogger<AuthenticationMiddleware> logger)
    {
        var opts = Options.Create(new SelfHostedOptions
        {
            ApiKey = ConfiguredKey,
            JwtSecret = "jwt-secret-at-least-32-characters-long-for-test!!",
            JwtIssuer = "test"
        });
        RequestDelegate next = _ => Task.CompletedTask;
        return new AuthenticationMiddleware(next, opts, logger);
    }

    private static DefaultHttpContext BuildContext(string path, string method, string? apiKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        if (apiKey is not null)
            ctx.Request.Headers["X-Api-Key"] = apiKey;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = Substitute.For<IServiceProvider>();
        return ctx;
    }

    // --- VERIFY 7.x: role claim emitted by the middleware ---

    [Fact]
    public async Task ValidLegacyKey_SetsLegacyApiKeyRole_NotSuperAdmin()
    {
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var middleware = BuildMiddleware(logger);
        var ctx = BuildContext("/api/v1/knowledge", "GET", ConfiguredKey);

        await middleware.InvokeAsync(ctx);

        // Principal populated, role is LegacyApiKey
        Assert.NotNull(ctx.User.Identity);
        Assert.True(ctx.User.Identity!.IsAuthenticated);
        Assert.True(ctx.User.IsInRole("LegacyApiKey"));
        Assert.False(ctx.User.IsInRole("SuperAdmin"));
        Assert.False(ctx.User.IsInRole("Admin"));

        // Belt-and-suspenders: pin the exact role-string claim value. If a future
        // builder renames both the middleware emission AND MakeContext arg in
        // AdminEndpointsAuthorizationTests, this Assert.Equal is the third
        // diff-site the reviewer must update — surfacing the invariant rename.
        var roleClaim = ctx.User.FindFirst(ClaimTypes.Role);
        Assert.NotNull(roleClaim);
        Assert.Equal("LegacyApiKey", roleClaim!.Value);

        // Short-form "role" claim also pinned — this is what some consumers read.
        var shortRoleClaim = ctx.User.FindFirst("role");
        Assert.NotNull(shortRoleClaim);
        Assert.Equal("LegacyApiKey", shortRoleClaim!.Value);
    }

    [Fact]
    public async Task ValidLegacyKey_EmitsDeprecationWarningLog()
    {
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var middleware = BuildMiddleware(logger);
        var ctx = BuildContext("/api/v1/knowledge", "GET", ConfiguredKey);

        await middleware.InvokeAsync(ctx);

        // Deprecation log captured (contains "deprecated" and the 2026-06-18 sunset).
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                             && o.ToString()!.Contains("2026-06-18", StringComparison.OrdinalIgnoreCase)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // --- VERIFY 7.1/7.2: authorization helpers reject the role ---

    [Fact]
    public void IsSuperAdmin_Rejects_LegacyApiKeyRole()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "LegacyApiKey") },
            "ApiKey");
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        Assert.False(AuthorizationHelpers.IsSuperAdmin(ctx));
        Assert.False(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void CanAssignRole_Rejects_LegacyApiKeyRole_ForAllTargets()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "LegacyApiKey") },
            "ApiKey");
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.User));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.Admin));
        Assert.False(AuthorizationHelpers.CanAssignRole(ctx, UserRole.SuperAdmin));
    }

    // --- Regression: wrong key still rejected, ksh_ keys not captured ---

    [Fact]
    public async Task WrongKey_DoesNotSetLegacyApiKeyRole()
    {
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var middleware = BuildMiddleware(logger);
        var ctx = BuildContext("/api/v1/knowledge", "GET", "not-the-real-key");

        await middleware.InvokeAsync(ctx);

        // User was never set; middleware returned 401.
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.False(ctx.User.IsInRole("LegacyApiKey"));
        Assert.False(ctx.User.IsInRole("SuperAdmin"));
    }

    [Fact]
    public async Task KshPrefixedKey_DoesNotUseLegacyPath()
    {
        // ksh_ keys are user API keys — the legacy branch must skip them.
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var middleware = BuildMiddleware(logger);
        var ctx = BuildContext("/api/v1/knowledge", "GET", "ksh_nomatchhere1234567890");

        await middleware.InvokeAsync(ctx);

        // Since no IAuthService is wired into our stub provider, the ksh path
        // will throw and be caught. The outcome we care about: legacy role is
        // NOT set (that was the bug we'd regress without this guard).
        Assert.False(ctx.User.IsInRole("LegacyApiKey"));
        Assert.False(ctx.User.IsInRole("SuperAdmin"));
    }
}
