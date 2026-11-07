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

/// <summary>
/// ADR_G6 (#1043): operator-configured allowlist via <c>Sync:AllowedPlatformHosts</c>.
/// Custom hosts pass only when configured; every existing SSRF guard still applies.
/// </summary>
public class PlatformUrlValidatorAllowlistTests
{
    private static PlatformUrlValidator MakeValidator(bool isDevelopment = false, params string[] allowedHosts)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");
        var options = Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SyncOptions
        {
            AllowedPlatformHosts = allowedHosts.ToList(),
        });
        return new PlatformUrlValidator(env, options);
    }

    [Theory]
    [InlineData("https://api.knowz.io")]
    [InlineData("https://api.dev.knowz.io")]
    public void DefaultCloudHosts_StillAllowed_WhenCustomAllowlistConfigured(string url)
    {
        var validator = MakeValidator(false, "peer.example.com");
        var result = validator.ValidatePlatformUrl(url);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("https://peer.example.com")]
    [InlineData("https://PEER.EXAMPLE.COM")]
    [InlineData("https://peer.example.com:8443/api")]
    public void CustomHost_Allowed_WhenConfigured(string url)
    {
        var validator = MakeValidator(false, "peer.example.com");
        var result = validator.ValidatePlatformUrl(url);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void AllowlistEntry_WrittenAsUrl_IsReducedToHost()
    {
        var validator = MakeValidator(false, "https://peer.example.com/");
        var result = validator.ValidatePlatformUrl("https://peer.example.com");
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void AllowlistEntries_AreTrimmed_AndBlanksIgnored()
    {
        var validator = MakeValidator(false, "  peer.example.com  ", "", "   ");
        Assert.True(validator.ValidatePlatformUrl("https://peer.example.com").IsValid);
    }

    [Theory]
    [InlineData("https://other.example.com")]
    [InlineData("https://peer.example.com.evil.example.com")] // substring attack
    [InlineData("https://evilpeer.example.com")]
    public void NonAllowlistedHost_Rejected_EvenWithCustomAllowlist(string url)
    {
        var validator = MakeValidator(false, "peer.example.com");
        var result = validator.ValidatePlatformUrl(url);
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NotAllowlisted, result.ErrorCode);
    }

    [Fact]
    public void NoOptions_BehavesAsCloudOnly()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var validator = new PlatformUrlValidator(env, options: null);
        Assert.True(validator.ValidatePlatformUrl("https://api.knowz.io").IsValid);
        Assert.Equal(UrlValidationErrorCode.NotAllowlisted,
            validator.ValidatePlatformUrl("https://peer.example.com").ErrorCode);
    }

    [Fact]
    public void CustomHost_HttpScheme_StillRejected_InProduction()
    {
        var validator = MakeValidator(false, "peer.example.com");
        var result = validator.ValidatePlatformUrl("http://peer.example.com");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NonHttpsScheme, result.ErrorCode);
    }

    [Theory]
    [InlineData("10.0.0.1", UrlValidationErrorCode.PrivateIpAddress)]
    [InlineData("192.168.1.10", UrlValidationErrorCode.PrivateIpAddress)]
    [InlineData("169.254.169.254", UrlValidationErrorCode.MetadataEndpoint)]
    [InlineData("localhost", UrlValidationErrorCode.LoopbackAddress)]
    [InlineData("127.0.0.1", UrlValidationErrorCode.LoopbackAddress)]
    public void SsrfGuards_StillApply_EvenWhenLiteralIsAllowlisted(string host, UrlValidationErrorCode expected)
    {
        var validator = MakeValidator(false, host);
        var result = validator.ValidatePlatformUrl($"https://{host}");
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public void DevLocalhostException_Unchanged_WithCustomAllowlist()
    {
        var validator = MakeValidator(true, "peer.example.com");
        Assert.True(validator.ValidatePlatformUrl("http://localhost:5000").IsValid);
    }
}


/// <summary>
/// ADR follow-up SEC_SyncAllowPrivatePeerAddresses: Sync:AllowPrivatePeerAddresses opt-in.
/// Default remains off; metadata stays blocked; allowlist is still required.
/// </summary>
public class PlatformUrlValidatorPrivatePeerOptInTests
{
    private static PlatformUrlValidator MakeValidator(
        bool allowPrivate,
        bool isDevelopment = false,
        params string[] allowedHosts)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");
        var options = Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SyncOptions
        {
            AllowedPlatformHosts = allowedHosts.ToList(),
            AllowPrivatePeerAddresses = allowPrivate,
        });
        return new PlatformUrlValidator(env, options);
    }

    [Theory]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://192.168.1.10")]
    [InlineData("http://10.0.0.1:8080")]
    [InlineData("http://127.0.0.1:15100")]
    [InlineData("http://localhost:15100")]
    public void PrivateOrLoopback_Allowed_WhenOptInAndAllowlisted(string url)
    {
        var host = new Uri(url).DnsSafeHost;
        var validator = MakeValidator(true, false, host);
        var result = validator.ValidatePlatformUrl(url);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("https://10.0.0.1")]
    [InlineData("http://127.0.0.1:15100")]
    public void PrivateOrLoopback_StillRejected_WhenOptInOff(string url)
    {
        var host = new Uri(url).DnsSafeHost;
        var validator = MakeValidator(false, false, host);
        var result = validator.ValidatePlatformUrl(url);
        Assert.False(result.IsValid);
        Assert.True(result.ErrorCode is UrlValidationErrorCode.PrivateIpAddress
            or UrlValidationErrorCode.LoopbackAddress
            or UrlValidationErrorCode.NonHttpsScheme);
    }

    [Fact]
    public void OptIn_DoesNotBypassAllowlist()
    {
        var validator = MakeValidator(true, false, "peer.example.com");
        var result = validator.ValidatePlatformUrl("https://10.0.0.1");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NotAllowlisted, result.ErrorCode);
    }

    [Fact]
    public void OptIn_StillRejectsMetadata()
    {
        var validator = MakeValidator(true, false, "169.254.169.254");
        var result = validator.ValidatePlatformUrl("https://169.254.169.254");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.MetadataEndpoint, result.ErrorCode);
    }

    [Fact]
    public void OptIn_DoesNotOpenHttp_ForBuiltInCloudHosts()
    {
        var validator = MakeValidator(true, false, "peer.example.com");
        var result = validator.ValidatePlatformUrl("http://api.dev.knowz.io");
        Assert.False(result.IsValid);
        Assert.Equal(UrlValidationErrorCode.NonHttpsScheme, result.ErrorCode);
    }

    [Fact]
    public void OptIn_AllowsHttp_ForAllowlistedCustomPeerHost()
    {
        var validator = MakeValidator(true, false, "knowz-selfhosted-g6-peer-api-1");
        var result = validator.ValidatePlatformUrl("http://knowz-selfhosted-g6-peer-api-1:8080");
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void OptIn_AllowsHttp_ForAllowlistedDockerInternalHost()
    {
        var validator = MakeValidator(true, false, "host.docker.internal");
        var result = validator.ValidatePlatformUrl("http://host.docker.internal:15200");
        Assert.True(result.IsValid, result.ErrorMessage);
    }
}
