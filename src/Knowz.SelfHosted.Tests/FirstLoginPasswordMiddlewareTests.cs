using Knowz.Core.Configuration;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Middleware;
using Knowz.SelfHosted.Application.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class FirstLoginPasswordMiddlewareTests
{
    private const string Secret = "strong-jwt-signing-secret-at-least-32-chars-for-test!!";

    [Theory]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/account/change-password")]
    public async Task ProvisionalJwt_AllowsOnlyFirstLoginIdentityAndPasswordRoutes(string path)
    {
        var nextCalled = false;
        var options = new SelfHostedOptions { JwtSecret = Secret, JwtIssuer = "test" };
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var token = JwtTokenHelper.GenerateToken(
            Guid.NewGuid(), "admin", Guid.NewGuid(), UserRole.SuperAdmin,
            DateTime.UtcNow.AddMinutes(5), Secret, "test", logger,
            mustChangePassword: true);
        var middleware = new AuthenticationMiddleware(
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(options), logger);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = path.EndsWith("change-password", StringComparison.Ordinal) ? "POST" : "GET";
        context.Request.Headers.Authorization = $"Bearer {token}";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("GET", "/api/v1/knowledge")]
    [InlineData("POST", "/api/v1/knowledge")]
    public async Task ProvisionalJwt_RejectsOrdinaryApiRoutes(string method, string path)
    {
        var nextCalled = false;
        var options = new SelfHostedOptions { JwtSecret = Secret, JwtIssuer = "test" };
        var logger = Substitute.For<ILogger<AuthenticationMiddleware>>();
        var token = JwtTokenHelper.GenerateToken(
            Guid.NewGuid(), "admin", Guid.NewGuid(), UserRole.SuperAdmin,
            DateTime.UtcNow.AddMinutes(5), Secret, "test", logger,
            mustChangePassword: true);
        var middleware = new AuthenticationMiddleware(
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(options), logger);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Headers.Authorization = $"Bearer {token}";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("PASSWORD_CHANGE_REQUIRED", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
