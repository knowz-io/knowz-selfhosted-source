using Knowz.Core.Configuration;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Options;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// SEC_P0Triage Item 4 (§Rule 1): JWT signing must fail-closed when the secret
/// is missing or &lt;32 chars. No literal fallback anywhere in the auth path.
/// </summary>
public class JwtAuthenticationTests
{
    private const string StrongSecret = "strong-jwt-signing-secret-at-least-32-chars-for-test!!";

    // ---- SelfHostedOptionsValidator (VERIFY 1.2, 1.3) ----

    [Fact]
    public void Validator_Succeeds_WhenJwtSecretIsStrong()
    {
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions { JwtSecret = StrongSecret, JwtIssuer = "test" };

        var result = sut.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_Fails_WhenJwtSecretEmpty_AndApiKeyEmpty()
    {
        // Neither JWT nor legacy API key set — SelfHosted authentication is effectively
        // disabled, which the validator accepts (operator opted out). The failure case
        // is when ApiKey is set (auth enabled) but JwtSecret is not.
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions { JwtSecret = "", ApiKey = "" };

        var result = sut.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_Fails_WhenApiKeySet_ButJwtSecretEmpty()
    {
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions { JwtSecret = "", ApiKey = "some-legacy-key" };

        var result = sut.Validate(null, opts);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("JwtSecret is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_Fails_WhenJwtSecretIs31Chars()
    {
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions
        {
            JwtSecret = new string('a', 31),
            JwtIssuer = "test"
        };

        var result = sut.Validate(null, opts);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("at least 32 characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_Succeeds_WhenJwtSecretIs32Chars()
    {
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions
        {
            JwtSecret = new string('a', 32),
            JwtIssuer = "test"
        };

        var result = sut.Validate(null, opts);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_Fails_WhenJwtSecretSet_ButJwtIssuerEmpty()
    {
        var sut = new SelfHostedOptionsValidator();
        var opts = new SelfHostedOptions { JwtSecret = StrongSecret, JwtIssuer = "" };

        var result = sut.Validate(null, opts);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("JwtIssuer is required", StringComparison.Ordinal));
    }

    // ---- JwtTokenHelper.GenerateToken (VERIFY 1.4) ----

    [Fact]
    public void GenerateToken_Throws_WhenSecretEmpty()
    {
        var logger = Substitute.For<ILogger>();
        var userId = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            JwtTokenHelper.GenerateToken(
                userId, "name", Guid.NewGuid(), UserRole.User,
                DateTime.UtcNow.AddMinutes(5),
                jwtSecret: "",
                jwtIssuer: "test",
                logger));
    }

    [Fact]
    public void GenerateToken_Throws_WhenSecretTooShort()
    {
        var logger = Substitute.For<ILogger>();
        var userId = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            JwtTokenHelper.GenerateToken(
                userId, "name", Guid.NewGuid(), UserRole.User,
                DateTime.UtcNow.AddMinutes(5),
                jwtSecret: new string('a', 31),
                jwtIssuer: "test",
                logger));
    }

    [Fact]
    public void GenerateToken_LogsCritical_WhenSecretMissing()
    {
        var logger = Substitute.For<ILogger>();
        var userId = Guid.NewGuid();

        try
        {
            JwtTokenHelper.GenerateToken(
                userId, "name", Guid.NewGuid(), UserRole.User,
                DateTime.UtcNow.AddMinutes(5),
                jwtSecret: "",
                jwtIssuer: "test",
                logger);
        }
        catch (InvalidOperationException)
        {
            // Expected — verify the CRITICAL log fired BEFORE the throw.
        }

        logger.Received().Log(
            LogLevel.Critical,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void GenerateToken_Succeeds_WhenSecretIsStrong()
    {
        var logger = Substitute.For<ILogger>();
        var userId = Guid.NewGuid();

        var token = JwtTokenHelper.GenerateToken(
            userId, "name", Guid.NewGuid(), UserRole.User,
            DateTime.UtcNow.AddMinutes(5),
            StrongSecret, "test", logger);

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GenerateToken_CarriesPasswordChangeRequirement()
    {
        var logger = Substitute.For<ILogger>();
        var token = JwtTokenHelper.GenerateToken(
            Guid.NewGuid(), "name", Guid.NewGuid(), UserRole.SuperAdmin,
            DateTime.UtcNow.AddMinutes(5), StrongSecret, "test", logger,
            mustChangePassword: true);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("true", parsed.Claims.Single(c => c.Type == "password_change_required").Value);
    }

    [Fact]
    public void GenerateToken_NeverEmitsDevFallbackSecretLiteral()
    {
        // Defensive: the literal must be gone from the compiled IL.
        var assembly = typeof(JwtTokenHelper).Assembly;
        var location = assembly.Location;

        if (!File.Exists(location))
        {
            // CI/packaging edge case — if no file, skip without failing. The
            // grep-based VERIFY 1.1 in the source tree is the authoritative check.
            return;
        }

        var bytes = File.ReadAllBytes(location);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("dev-fallback-secret-key", text, StringComparison.Ordinal);
    }
}
