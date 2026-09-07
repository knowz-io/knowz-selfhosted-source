using Knowz.SelfHosted.Application.Validators;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// V-SEC-01 allowlist + SSRF guard coverage for <see cref="PlatformUrlValidator"/>.
/// </summary>
public class PlatformUrlValidatorTests
{
    private static PlatformUrlValidator MakeValidator(bool isDevelopment = false)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");
        return new PlatformUrlValidator(env);
    }

    [Theory]
    [InlineData("https://api.knowz.io")]
    [InlineData("https://api.dev.knowz.io")]
    public void ValidatePlatformUrl_Allowlisted_Passes(string url)
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl(url);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void ValidatePlatformUrl_HttpScheme_Rejected_InProduction()
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl("http://api.knowz.io");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NonHttpsScheme, result.ErrorCode);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://api.knowz.io.evil.example.com")] // substring attack
    [InlineData("https://fakeapi.knowz.io")]
    public void ValidatePlatformUrl_NonAllowlistedHost_Rejected(string url)
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl(url);
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NotAllowlisted, result.ErrorCode);
    }

    [Theory]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://192.168.1.1")]
    public void ValidatePlatformUrl_PrivateIp_Rejected(string url)
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl(url);
        Assert.False(result.IsValid);
        // Not allowlisted is checked first for hosts with a DNS name; literals fall through.
        Assert.True(result.ErrorCode is UrlValidationErrorCode.NotAllowlisted or UrlValidationErrorCode.PrivateIpAddress);
    }

    [Fact]
    public void ValidatePlatformUrl_MetadataEndpoint_Rejected()
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl("https://169.254.169.254");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidatePlatformUrl_Localhost_Rejected_InProduction()
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl("https://localhost");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.LoopbackAddress, result.ErrorCode);
    }

    [Fact]
    public void ValidatePlatformUrl_LocalhostHttp_Allowed_InDevelopment()
    {
        var validator = MakeValidator(isDevelopment: true);
        var result = validator.ValidatePlatformUrl("http://localhost:5000");
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void ValidatePlatformUrl_Empty_Rejected()
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl("");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.InvalidFormat, result.ErrorCode);
    }

    [Fact]
    public void ValidatePlatformUrl_Malformed_Rejected()
    {
        var validator = MakeValidator();
        var result = validator.ValidatePlatformUrl("not-a-url");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.InvalidFormat, result.ErrorCode);
    }
}
