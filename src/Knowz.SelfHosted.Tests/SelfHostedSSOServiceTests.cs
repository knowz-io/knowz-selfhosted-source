using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class SelfHostedSSOServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly SelfHostedOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SelfHostedSSOService> _logger;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public SelfHostedSSOServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _options = new SelfHostedOptions
        {
            TenantId = TenantId,
            JwtSecret = "this-is-a-test-secret-key-at-least-32-characters",
            JwtExpirationMinutes = 60,
            JwtIssuer = "test-issuer"
        };

        var tenantProvider = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient().Returns(new HttpClient());
        _logger = Substitute.For<ILogger<SelfHostedSSOService>>();

        // Seed default tenant
        _db.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Default",
            Slug = "default",
            IsActive = true,
        });
        _db.SaveChanges();

        // Clean static state between tests
        SelfHostedSSOService.ClearStateStore();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        SelfHostedSSOService.ClearStateStore();
    }

    private SelfHostedSSOService CreateService(Dictionary<string, string?>? configOverrides = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "false",
        };

        if (configOverrides != null)
        {
            foreach (var kvp in configOverrides)
                configData[kvp.Key] = kvp.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new SelfHostedSSOService(
            _db,
            configuration,
            _httpClientFactory,
            _logger,
            Options.Create(_options));
    }

    // --- GetEnabledProvidersAsync ---

    [Fact]
    public async Task GetEnabledProviders_ReturnsEmptyList_WhenSSODisabled()
    {
        var service = CreateService();
        var result = await service.GetEnabledProvidersAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEnabledProviders_ReturnsEmptyList_WhenEnabledButNoProvidersConfigured()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true"
        });

        var result = await service.GetEnabledProvidersAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEnabledProviders_ReturnsMicrosoft_WhenMicrosoftConfigured()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:ClientSecret"] = "test-secret",
        });

        var result = await service.GetEnabledProvidersAsync();

        Assert.Single(result);
        Assert.Equal("Microsoft", result[0].Provider);
        Assert.Equal("Sign in with Microsoft", result[0].DisplayName);
    }

    [Fact]
    public async Task GetEnabledProviders_ReturnsGoogle_WhenGoogleConfigured()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Google:ClientId"] = "test-google-client-id",
        });

        var result = await service.GetEnabledProvidersAsync();

        Assert.Single(result);
        Assert.Equal("Google", result[0].Provider);
        Assert.Equal("Sign in with Google", result[0].DisplayName);
    }

    [Fact]
    public async Task GetEnabledProviders_ReturnsBothProviders_WhenBothConfigured()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
            ["SSO:Google:ClientId"] = "google-client-id",
        });

        var result = await service.GetEnabledProvidersAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Provider == "Microsoft");
        Assert.Contains(result, p => p.Provider == "Google");
    }

    // --- GenerateAuthorizeUrlAsync ---

    [Fact]
    public async Task GenerateAuthorizeUrl_ReturnsError_WhenSSODisabled()
    {
        var service = CreateService();
        var result = await service.GenerateAuthorizeUrlAsync("Microsoft", "http://localhost/callback");

        Assert.False(result.Success);
        Assert.Equal("SSO is not enabled", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateAuthorizeUrl_ReturnsError_WhenProviderNotConfigured()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
        });

        var result = await service.GenerateAuthorizeUrlAsync("Microsoft", "http://localhost/callback");

        Assert.False(result.Success);
        Assert.Contains("not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateAuthorizeUrl_ReturnsError_WhenUnknownProvider()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
        });

        var result = await service.GenerateAuthorizeUrlAsync("Unknown", "http://localhost/callback");

        Assert.False(result.Success);
    }

    // Guard: OIDC redirect_uri must be absolute. A relative path here would
    // be forwarded verbatim to the IdP (Entra returns AADSTS50011, Google
    // returns invalid_request) and would also poison the token-exchange POST.
    [Theory]
    [InlineData("/auth/sso/callback")]
    [InlineData("auth/sso/callback")]
    [InlineData("//evil.example.com/callback")]
    [InlineData("")]
    public async Task GenerateAuthorizeUrl_ReturnsError_WhenRedirectUriIsNotAbsolute(string redirectUri)
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        var result = await service.GenerateAuthorizeUrlAsync("Microsoft", redirectUri);

        Assert.False(result.Success);
        Assert.Contains("absolute", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // --- GetProviderConfig ---

    [Fact]
    public void GetProviderConfig_ReturnsMicrosoftCommonAuthority_WhenNoDirectoryTenantId()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
        });

        var (clientId, authority) = service.GetProviderConfig("Microsoft");

        Assert.Equal("ms-client-id", clientId);
        Assert.Equal("https://login.microsoftonline.com/common/v2.0", authority);
    }

    [Fact]
    public void GetProviderConfig_ReturnsMicrosoftTenantAuthority_WhenDirectoryTenantIdSet()
    {
        var tenantGuid = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = tenantGuid.ToString(),
        });

        var (clientId, authority) = service.GetProviderConfig("Microsoft");

        Assert.Equal("ms-client-id", clientId);
        Assert.Equal($"https://login.microsoftonline.com/{tenantGuid}/v2.0", authority);
    }

    [Fact]
    public void GetProviderConfig_ReturnsGoogleAuthority()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Google:ClientId"] = "google-client-id",
        });

        var (clientId, authority) = service.GetProviderConfig("Google");

        Assert.Equal("google-client-id", clientId);
        Assert.Equal("https://accounts.google.com", authority);
    }

    [Fact]
    public void GetProviderConfig_ReturnsNull_ForUnknownProvider()
    {
        var service = CreateService();

        var (clientId, authority) = service.GetProviderConfig("Unknown");

        Assert.Null(clientId);
        Assert.Equal("", authority);
    }

    // --- GetClientSecret ---

    [Fact]
    public void GetClientSecret_ReturnsMicrosoftSecret()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        Assert.Equal("ms-secret", service.GetClientSecret("Microsoft"));
    }

    [Fact]
    public void GetClientSecret_ReturnsGoogleSecret()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Google:ClientSecret"] = "google-secret",
        });

        Assert.Equal("google-secret", service.GetClientSecret("Google"));
    }

    [Fact]
    public void GetClientSecret_ReturnsNull_ForUnknownProvider()
    {
        var service = CreateService();
        Assert.Null(service.GetClientSecret("Unknown"));
    }

    // --- PKCE Helpers ---

    [Fact]
    public void GenerateCodeVerifier_ProducesBase64UrlSafeString()
    {
        var verifier = SelfHostedSSOService.GenerateCodeVerifier();

        Assert.NotEmpty(verifier);
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_ProducesUniqueValues()
    {
        var v1 = SelfHostedSSOService.GenerateCodeVerifier();
        var v2 = SelfHostedSSOService.GenerateCodeVerifier();

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void GenerateCodeChallenge_ProducesBase64UrlSafeString()
    {
        var verifier = SelfHostedSSOService.GenerateCodeVerifier();
        var challenge = SelfHostedSSOService.GenerateCodeChallenge(verifier);

        Assert.NotEmpty(challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
        Assert.DoesNotContain("=", challenge);
    }

    [Fact]
    public void GenerateCodeChallenge_IsDeterministic_ForSameVerifier()
    {
        var verifier = SelfHostedSSOService.GenerateCodeVerifier();
        var c1 = SelfHostedSSOService.GenerateCodeChallenge(verifier);
        var c2 = SelfHostedSSOService.GenerateCodeChallenge(verifier);

        Assert.Equal(c1, c2);
    }

    [Fact]
    public void GenerateCodeChallenge_DiffersFromVerifier()
    {
        var verifier = SelfHostedSSOService.GenerateCodeVerifier();
        var challenge = SelfHostedSSOService.GenerateCodeChallenge(verifier);

        Assert.NotEqual(verifier, challenge);
    }

    [Fact]
    public void GenerateSecureRandomString_ProducesCorrectLength()
    {
        var str = SelfHostedSSOService.GenerateSecureRandomString(32);
        Assert.Equal(32, str.Length);
    }

    [Fact]
    public void GenerateSecureRandomString_ProducesUniqueValues()
    {
        var s1 = SelfHostedSSOService.GenerateSecureRandomString(32);
        var s2 = SelfHostedSSOService.GenerateSecureRandomString(32);

        Assert.NotEqual(s1, s2);
    }

    // --- State Store ---

    [Fact]
    public void CleanupExpiredStates_RemovesExpiredEntries()
    {
        SelfHostedSSOService.StateStore["expired"] = (new SSOStateData
        {
            Provider = "Microsoft",
            RedirectUri = "http://localhost",
            CodeVerifier = "verifier",
            Nonce = "nonce",
        }, DateTime.UtcNow.AddMinutes(-5)); // Already expired

        SelfHostedSSOService.StateStore["valid"] = (new SSOStateData
        {
            Provider = "Google",
            RedirectUri = "http://localhost",
            CodeVerifier = "verifier2",
            Nonce = "nonce2",
        }, DateTime.UtcNow.AddMinutes(5)); // Still valid

        SelfHostedSSOService.CleanupExpiredStates();

        Assert.False(SelfHostedSSOService.StateStore.ContainsKey("expired"));
        Assert.True(SelfHostedSSOService.StateStore.ContainsKey("valid"));
    }

    // --- HandleCallbackAsync ---

    [Fact]
    public async Task HandleCallback_ReturnsError_WhenStateNotFound()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        var result = await service.HandleCallbackAsync("auth-code", "nonexistent-state");

        Assert.False(result.Success);
        Assert.Equal("Invalid or expired state", result.ErrorMessage);
    }

    [Fact]
    public async Task HandleCallback_ReturnsError_WhenStateExpired()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        // Add an expired state
        SelfHostedSSOService.StateStore["test-state"] = (new SSOStateData
        {
            Provider = "Microsoft",
            RedirectUri = "http://localhost/callback",
            CodeVerifier = "verifier",
            Nonce = "nonce",
        }, DateTime.UtcNow.AddMinutes(-5)); // Expired

        var result = await service.HandleCallbackAsync("auth-code", "test-state");

        Assert.False(result.Success);
        Assert.Equal("Invalid or expired state", result.ErrorMessage);
    }

    [Fact]
    public async Task HandleCallback_RemovesState_EvenOnSuccess()
    {
        // We just verify state is removed after retrieval (single-use)
        var state = "single-use-state";
        SelfHostedSSOService.StateStore[state] = (new SSOStateData
        {
            Provider = "Microsoft",
            RedirectUri = "http://localhost/callback",
            CodeVerifier = "verifier",
            Nonce = "nonce",
        }, DateTime.UtcNow.AddMinutes(10));

        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        // This will fail at token exchange (no real OIDC endpoint), but the state should be consumed
        await service.HandleCallbackAsync("auth-code", state);

        Assert.False(SelfHostedSSOService.StateStore.ContainsKey(state));
    }

    // --- FindOrCreateUserAsync ---

    [Fact]
    public async Task FindOrCreateUser_MatchesByOAuthSubjectId()
    {
        var existingUser = new User
        {
            TenantId = TenantId,
            Username = "existing@example.com",
            Email = "existing@example.com",
            PasswordHash = "SSO_ONLY_NO_PASSWORD",
            OAuthProvider = "Microsoft",
            OAuthSubjectId = "sub-12345",
            OAuthEmail = "existing@example.com",
            IsActive = true,
        };
        _db.Users.Add(existingUser);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var claims = new ValidatedTokenClaims
        {
            SubjectId = "sub-12345",
            Email = "existing@example.com",
            DisplayName = "Existing User"
        };

        var (user, wasProvisioned) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        Assert.False(wasProvisioned);
        Assert.Equal(existingUser.Id, user!.Id);
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task FindOrCreateUser_MatchesByEmail_AndPopulatesOAuthFields()
    {
        var existingUser = new User
        {
            TenantId = TenantId,
            Username = "emailuser",
            Email = "emailuser@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password"),
            IsActive = true,
        };
        _db.Users.Add(existingUser);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var claims = new ValidatedTokenClaims
        {
            SubjectId = "new-sub-id",
            Email = "emailuser@example.com",
            DisplayName = "Email User"
        };

        var (user, wasProvisioned) = await service.FindOrCreateUserAsync(claims, "Google");

        Assert.NotNull(user);
        Assert.False(wasProvisioned);
        Assert.Equal("Google", user!.OAuthProvider);
        Assert.Equal("new-sub-id", user.OAuthSubjectId);
        Assert.Equal("emailuser@example.com", user.OAuthEmail);
    }

    [Fact]
    public async Task FindOrCreateUser_AutoProvisions_WhenEnabled()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
            ["SSO:DefaultRole"] = "User",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "new-sub-id",
            Email = "newuser@example.com",
            DisplayName = "New User",
            GivenName = "New",
            FamilyName = "User"
        };

        var (user, wasProvisioned) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        Assert.True(wasProvisioned);
        Assert.Equal("newuser@example.com", user!.Email);
        Assert.Equal("newuser@example.com", user.Username);
        Assert.Equal("New User", user.DisplayName);
        Assert.Equal("SSO_ONLY_NO_PASSWORD", user.PasswordHash);
        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal("Microsoft", user.OAuthProvider);
        Assert.Equal("new-sub-id", user.OAuthSubjectId);
        Assert.True(user.IsActive);
    }

    [Fact]
    public async Task FindOrCreateUser_ReturnsNull_WhenAutoProvisionDisabledAndNoMatch()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "false",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "unknown-sub",
            Email = "unknown@example.com",
        };

        var (user, wasProvisioned) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.Null(user);
        Assert.False(wasProvisioned);
    }

    [Fact]
    public async Task FindOrCreateUser_AutoProvision_UsesDefaultRoleFromConfig()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
            ["SSO:DefaultRole"] = "Admin",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "admin-sub",
            Email = "admin@example.com",
            DisplayName = "Admin SSO"
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Google");

        Assert.NotNull(user);
        Assert.Equal(UserRole.Admin, user!.Role);
    }

    [Fact]
    public async Task FindOrCreateUser_AutoProvision_FallsBackToUserRole_WhenInvalidConfig()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
            ["SSO:DefaultRole"] = "InvalidRole",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "fallback-sub",
            Email = "fallback@example.com",
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        Assert.Equal(UserRole.User, user!.Role);
    }

    [Fact]
    public async Task FindOrCreateUser_DoesNotMatch_InactiveUsers()
    {
        var inactiveUser = new User
        {
            TenantId = TenantId,
            Username = "inactive@example.com",
            Email = "inactive@example.com",
            PasswordHash = "hash",
            OAuthProvider = "Microsoft",
            OAuthSubjectId = "inactive-sub",
            IsActive = false,
        };
        _db.Users.Add(inactiveUser);
        await _db.SaveChangesAsync();

        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "false",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "inactive-sub",
            Email = "inactive@example.com",
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.Null(user);
    }

    [Fact]
    public async Task FindOrCreateUser_SSOUser_CannotLoginWithPassword()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
            ["SSO:DefaultRole"] = "User",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "sso-sub",
            Email = "sso@example.com",
            DisplayName = "SSO User"
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        // Verify that SSO_ONLY_NO_PASSWORD cannot be verified by BCrypt
        Assert.Throws<BCrypt.Net.SaltParseException>(() =>
            BCrypt.Net.BCrypt.Verify("anything", user!.PasswordHash));
    }

    [Fact]
    public async Task FindOrCreateUser_NormalizesEmail_ToLowerCase()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "mixed-case-sub",
            Email = "MixedCase@EXAMPLE.com",
            DisplayName = "Mixed Case"
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        Assert.Equal("mixedcase@example.com", user!.Email);
        Assert.Equal("mixedcase@example.com", user.OAuthEmail);
    }

    [Fact]
    public async Task FindOrCreateUser_UsesEmailAsDisplayName_WhenDisplayNameNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "no-name-sub",
            Email = "noname@example.com",
            DisplayName = null,
            GivenName = null,
            FamilyName = null
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Google");

        Assert.NotNull(user);
        Assert.Equal("noname@example.com", user!.DisplayName);
    }

    [Fact]
    public async Task FindOrCreateUser_UsesGivenAndFamilyName_WhenDisplayNameNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:AutoProvisionUsers"] = "true",
        });

        var claims = new ValidatedTokenClaims
        {
            SubjectId = "name-parts-sub",
            Email = "parts@example.com",
            DisplayName = null,
            GivenName = "John",
            FamilyName = "Doe"
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Microsoft");

        Assert.NotNull(user);
        Assert.Equal("John Doe", user!.DisplayName);
    }

    [Fact]
    public async Task FindOrCreateUser_OAuthSubjectIdMatch_SyncsEmail()
    {
        var existingUser = new User
        {
            TenantId = TenantId,
            Username = "olduser",
            Email = "old@example.com",
            PasswordHash = "SSO_ONLY_NO_PASSWORD",
            OAuthProvider = "Google",
            OAuthSubjectId = "google-sub-123",
            OAuthEmail = "old@example.com",
            IsActive = true,
        };
        _db.Users.Add(existingUser);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var claims = new ValidatedTokenClaims
        {
            SubjectId = "google-sub-123",
            Email = "new@example.com", // Email changed
        };

        var (user, _) = await service.FindOrCreateUserAsync(claims, "Google");

        Assert.NotNull(user);
        Assert.Equal("new@example.com", user!.OAuthEmail); // Email synced
    }

    // --- DetectSSOMode ---

    [Fact]
    public void DetectSSOMode_ReturnsDisabled_WhenSSONotEnabled()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "false",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:ClientSecret"] = "test-secret",
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.Disabled, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsDisabled_WhenNoClientId()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.Disabled, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsConfidentialClient_WhenClientSecretPresent()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:ClientSecret"] = "test-secret",
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.ConfidentialClient, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsPkcePublicClient_WhenNoSecretButTenantIdPresent()
    {
        var tenantId = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = tenantId.ToString(),
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.PkcePublicClient, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsDisabled_WhenNoSecretAndNoTenantId()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.Disabled, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsDisabled_ForNonMicrosoftProvider()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Google:ClientId"] = "google-id",
            ["SSO:Google:ClientSecret"] = "google-secret",
        });

        var mode = service.DetectSSOMode("Google");
        Assert.Equal(SSOMode.Disabled, mode);
    }

    [Fact]
    public void DetectSSOMode_ReturnsPkcePublicClient_WithCSVTenantIds()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = $"{Guid.NewGuid()},{Guid.NewGuid()}",
        });

        var mode = service.DetectSSOMode("Microsoft");
        Assert.Equal(SSOMode.PkcePublicClient, mode);
    }

    // --- ParseAllowedTenantIds ---

    [Fact]
    public void ParseAllowedTenantIds_ReturnsEmptyList_WhenNotConfigured()
    {
        var service = CreateService();
        var result = service.ParseAllowedTenantIds();
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAllowedTenantIds_ParsesSingleGuid()
    {
        var guid = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = guid.ToString(),
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Single(result);
        Assert.Equal(guid, result[0]);
    }

    [Fact]
    public void ParseAllowedTenantIds_ParsesCSVGuids()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = $"{guid1},{guid2}",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Equal(2, result.Count);
        Assert.Contains(guid1, result);
        Assert.Contains(guid2, result);
    }

    [Fact]
    public void ParseAllowedTenantIds_TrimsWhitespace()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = $" {guid1} , {guid2} ",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Equal(2, result.Count);
        Assert.Contains(guid1, result);
        Assert.Contains(guid2, result);
    }

    [Fact]
    public void ParseAllowedTenantIds_SkipsInvalidEntries()
    {
        var guid = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = $"{guid},not-a-guid,also-invalid",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Single(result);
        Assert.Equal(guid, result[0]);
    }

    [Fact]
    public void ParseAllowedTenantIds_DeduplicatesGuids()
    {
        var guid = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = $"{guid},{guid}",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Single(result);
    }

    [Fact]
    public void ParseAllowedTenantIds_ReturnsEmptyList_WhenEmpty()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = "",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAllowedTenantIds_ReturnsEmptyList_WhenWhitespaceOnly()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:DirectoryTenantId"] = "  , ,  ",
        });

        var result = service.ParseAllowedTenantIds();
        Assert.Empty(result);
    }

    // --- GetMicrosoftAuthority with CSV support ---

    [Fact]
    public void GetProviderConfig_UsesCommonAuthority_ForMultipleTenantIds()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = $"{guid1},{guid2}",
        });

        var (_, authority) = service.GetProviderConfig("Microsoft");
        Assert.Equal("https://login.microsoftonline.com/common/v2.0", authority);
    }

    [Fact]
    public void GetProviderConfig_UsesTenantSpecificAuthority_ForSingleTenantId()
    {
        var guid = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = guid.ToString(),
        });

        var (_, authority) = service.GetProviderConfig("Microsoft");
        Assert.Equal($"https://login.microsoftonline.com/{guid}/v2.0", authority);
    }

    // --- GetEnabledProvidersAsync with Mode ---

    [Fact]
    public async Task GetEnabledProviders_IncludesMode_ConfidentialClient()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:ClientSecret"] = "ms-secret",
        });

        var result = await service.GetEnabledProvidersAsync();
        Assert.Single(result);
        Assert.Equal("ConfidentialClient", result[0].Mode);
    }

    [Fact]
    public async Task GetEnabledProviders_IncludesMode_PkcePublicClient()
    {
        var tenantId = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = tenantId.ToString(),
        });

        var result = await service.GetEnabledProvidersAsync();
        Assert.Single(result);
        Assert.Equal("PkcePublicClient", result[0].Mode);
    }

    [Fact]
    public async Task GetEnabledProviders_GoogleAlwaysConfidentialMode()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Google:ClientId"] = "google-client-id",
        });

        var result = await service.GetEnabledProvidersAsync();
        Assert.Single(result);
        Assert.Equal("ConfidentialClient", result[0].Mode);
    }

    [Fact]
    public async Task GetEnabledProviders_MicrosoftNotShown_WhenNoSecretAndNoTenantId()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "ms-client-id",
            // No secret, no tenant ID -> mode is Disabled -> not shown
        });

        var result = await service.GetEnabledProvidersAsync();
        Assert.Empty(result);
    }

    // --- HandleCallbackAsync PKCE mode ---

    [Fact]
    public async Task HandleCallback_PkceMode_DoesNotRequireClientSecret()
    {
        var tenantId = Guid.NewGuid();
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:DirectoryTenantId"] = tenantId.ToString(),
            // No ClientSecret -> PKCE mode
        });

        // Add a valid state
        var state = "pkce-test-state";
        SelfHostedSSOService.StateStore[state] = (new SSOStateData
        {
            Provider = "Microsoft",
            RedirectUri = "http://localhost/callback",
            CodeVerifier = "test-verifier",
            Nonce = "test-nonce",
        }, DateTime.UtcNow.AddMinutes(10));

        // The call will proceed past config validation but fail at OIDC discovery (no network).
        // We catch the exception or check the result -- the key assertion is it does NOT fail
        // with "SSO configuration incomplete".
        try
        {
            var result = await service.HandleCallbackAsync("auth-code", state);
            // If we get a result, verify it's not a config error
            Assert.False(result.Success);
            Assert.NotEqual("SSO configuration incomplete", result.ErrorMessage);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unable to obtain configuration"))
        {
            // Expected: OIDC discovery network error means we got past config validation
        }
    }

    [Fact]
    public async Task HandleCallback_ConfidentialMode_RequiresClientSecret()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SSO:Enabled"] = "true",
            ["SSO:Microsoft:ClientId"] = "test-client-id",
            ["SSO:Microsoft:ClientSecret"] = "test-secret",
        });

        var state = "conf-test-state";
        SelfHostedSSOService.StateStore[state] = (new SSOStateData
        {
            Provider = "Microsoft",
            RedirectUri = "http://localhost/callback",
            CodeVerifier = "test-verifier",
            Nonce = "test-nonce",
        }, DateTime.UtcNow.AddMinutes(10));

        // Will fail at token exchange, but should get past config check
        var result = await service.HandleCallbackAsync("auth-code", state);
        Assert.False(result.Success);
        Assert.NotEqual("SSO configuration incomplete", result.ErrorMessage);
    }

    // --- ValidatedTokenClaims.TenantId ---

    [Fact]
    public void ValidatedTokenClaims_HasTenantIdProperty()
    {
        var claims = new ValidatedTokenClaims
        {
            SubjectId = "sub-1",
            Email = "test@example.com",
            TenantId = "tenant-guid-value",
        };

        Assert.Equal("tenant-guid-value", claims.TenantId);
    }
}
