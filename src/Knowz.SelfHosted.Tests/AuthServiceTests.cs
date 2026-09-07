using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IAuthService _authService;
    private readonly SelfHostedOptions _options;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Seed password that must pass <see cref="AuthService.IsWeakPassword"/>:
    /// 16 chars, upper + lower + digit + symbol, no denylist fragment.
    /// </summary>
    private const string TestStrongPassword = "R4pid!Vault-Seed";

    public AuthServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _options = new SelfHostedOptions
        {
            TenantId = TenantId,
            // SuperAdmin seed creds — strong enough to pass AuthService.IsWeakPassword
            // (>=12 chars, upper+lower+digit+symbol, not on the denylist). The literal
            // "changeme" default was removed in SEC_P0Triage §Rule 3.
            SuperAdminUsername = "svc-seed-test",
            SuperAdminPassword = TestStrongPassword,
            JwtSecret = "this-is-a-test-secret-key-at-least-32-characters",
            JwtExpirationMinutes = 60,
            JwtIssuer = "test-issuer"
        };

        var selfHostedOptions = Options.Create(_options);
        var tenantProvider = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);
        var logger = Substitute.For<ILogger<AuthService>>();

        _authService = new AuthService(_db, selfHostedOptions, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Password Hashing ---

    [Fact]
    public void Should_HashPassword_WhenGivenPlaintext()
    {
        var hash = _authService.HashPassword("testpassword");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.NotEqual("testpassword", hash);
    }

    [Fact]
    public void Should_VerifyPassword_WhenHashMatches()
    {
        var hash = _authService.HashPassword("mypassword");

        Assert.True(_authService.VerifyPassword("mypassword", hash));
    }

    [Fact]
    public void Should_RejectPassword_WhenHashDoesNotMatch()
    {
        var hash = _authService.HashPassword("mypassword");

        Assert.False(_authService.VerifyPassword("wrongpassword", hash));
    }

    [Fact]
    public void Should_ProduceDifferentHashes_ForSamePassword()
    {
        var hash1 = _authService.HashPassword("samepassword");
        var hash2 = _authService.HashPassword("samepassword");

        Assert.NotEqual(hash1, hash2); // BCrypt salting
    }

    // --- EnsureSuperAdminExistsAsync ---

    [Fact]
    public async Task Should_CreateSuperAdmin_WhenNoneExists()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var users = await _db.Users.ToListAsync();
        Assert.Single(users);
        Assert.Equal(_options.SuperAdminUsername, users[0].Username);
        Assert.Equal(UserRole.SuperAdmin, users[0].Role);
        Assert.True(users[0].IsActive);
    }

    [Fact]
    public async Task Should_MarkOnlyNewGeneratedPasswordSuperAdmin_ForFirstLoginChange()
    {
        _options.RequireSuperAdminPasswordChange = true;

        await _authService.EnsureSuperAdminExistsAsync();
        var seeded = await _db.Users.SingleAsync();

        Assert.True(seeded.MustChangePassword);
        var login = await _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword);
        Assert.True(login.User.MustChangePassword);

        _options.RequireSuperAdminPasswordChange = false;
        await _authService.EnsureSuperAdminExistsAsync();
        Assert.True((await _db.Users.SingleAsync()).MustChangePassword);
    }

    [Fact]
    public async Task Should_CreateDefaultTenant_WhenSeedingSuperAdmin()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var tenants = await _db.Tenants.ToListAsync();
        Assert.Single(tenants);
        Assert.Equal("Default", tenants[0].Name);
        Assert.Equal("default", tenants[0].Slug);
    }

    [Fact]
    public async Task Should_NotCreateDuplicate_WhenSuperAdminAlreadyExists()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        await _authService.EnsureSuperAdminExistsAsync(); // Call twice

        var users = await _db.Users.Where(u => u.Role == UserRole.SuperAdmin).ToListAsync();
        Assert.Single(users);
    }

    [Fact]
    public async Task Should_HashSuperAdminPassword_NotStorePlaintext()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var user = await _db.Users.FirstAsync();
        Assert.NotEqual(TestStrongPassword, user.PasswordHash);
        Assert.True(_authService.VerifyPassword(TestStrongPassword, user.PasswordHash));
    }

    [Fact]
    public async Task Should_FixExistingAdminRole_WhenSuperAdminMissing()
    {
        // Simulate legacy DB state: the configured admin exists with Role=0
        // (old enum SuperAdmin=0, post-SwapUserRoleValues it's User=0).
        var tenant = new Tenant
        {
            Name = "Default", Slug = "default", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        var user = new User
        {
            TenantId = tenant.Id, Username = _options.SuperAdminUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(TestStrongPassword),
            DisplayName = "Admin", Role = UserRole.User, // Wrong role — the bug
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await _authService.EnsureSuperAdminExistsAsync();

        var repaired = await _db.Users.FirstAsync(u => u.Username == _options.SuperAdminUsername);
        Assert.Equal(UserRole.SuperAdmin, repaired.Role);
        // Should NOT create a duplicate
        Assert.Single(await _db.Users.ToListAsync());
    }

    // --- UserRole Enum Stability ---

    [Fact]
    public void UserRole_Values_MustNotChange()
    {
        // These values are persisted in the database. Changing them silently breaks
        // existing users' roles. If this test fails, you've changed the enum — DON'T.
        Assert.Equal(0, (int)UserRole.User);
        Assert.Equal(1, (int)UserRole.Admin);
        Assert.Equal(2, (int)UserRole.SuperAdmin);
    }

    // --- LoginAsync ---

    [Fact]
    public async Task Should_ReturnAuthResult_WhenCredentialsValid()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var result = await _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Token);
        Assert.True(result.ExpiresAt > DateTime.UtcNow);
        Assert.Equal(_options.SuperAdminUsername, result.User.Username);
        Assert.Equal(UserRole.SuperAdmin, result.User.Role);
    }

    [Fact]
    public async Task Should_ThrowUnauthorized_WhenUsernameNotFound()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _authService.LoginAsync("nonexistent", "password"));
    }

    [Fact]
    public async Task Should_ThrowUnauthorized_WhenPasswordWrong()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _authService.LoginAsync(_options.SuperAdminUsername, "wrongpassword"));
    }

    [Fact]
    public async Task Should_ThrowUnauthorized_WhenUserInactive()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        var user = await _db.Users.FirstAsync();
        user.IsActive = false;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword));
    }

    [Fact]
    public async Task Should_UpdateLastLoginAt_OnSuccessfulLogin()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        var beforeLogin = DateTime.UtcNow;

        await _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword);

        var user = await _db.Users.FirstAsync();
        Assert.NotNull(user.LastLoginAt);
        Assert.True(user.LastLoginAt >= beforeLogin);
    }

    [Fact]
    public async Task Should_IncludeTenantName_InLoginResult()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var result = await _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword);

        Assert.Equal("Default", result.User.TenantName);
    }

    // --- ValidateApiKeyAsync ---

    [Fact]
    public async Task Should_ReturnAuthResult_WhenApiKeyValid()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        var user = await _db.Users.FirstAsync();
        user.ApiKey = "ksh_testapikey12345678901234567890";
        await _db.SaveChangesAsync();

        var result = await _authService.ValidateApiKeyAsync("ksh_testapikey12345678901234567890");

        Assert.NotNull(result);
        Assert.Equal(_options.SuperAdminUsername, result!.User.Username);
        Assert.NotEmpty(result.Token);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenApiKeyNotFound()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var result = await _authService.ValidateApiKeyAsync("ksh_nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenApiKeyUserInactive()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        var user = await _db.Users.FirstAsync();
        user.ApiKey = "ksh_testapikey12345678901234567890";
        user.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await _authService.ValidateApiKeyAsync("ksh_testapikey12345678901234567890");

        Assert.Null(result);
    }

    // --- GetCurrentUserAsync ---

    [Fact]
    public async Task Should_ReturnUserDto_WhenUserExists()
    {
        await _authService.EnsureSuperAdminExistsAsync();
        var user = await _db.Users.FirstAsync();

        var result = await _authService.GetCurrentUserAsync(user.Id);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result!.Id);
        Assert.Equal(_options.SuperAdminUsername, result.Username);
    }

    [Fact]
    public async Task Should_ReturnNull_WhenUserDoesNotExist()
    {
        var result = await _authService.GetCurrentUserAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    // --- JWT Token Structure ---

    [Fact]
    public async Task Should_GenerateValidJwt_WithExpectedClaims()
    {
        await _authService.EnsureSuperAdminExistsAsync();

        var result = await _authService.LoginAsync(_options.SuperAdminUsername, TestStrongPassword);

        // Parse the JWT to verify claims
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(result.Token);

        Assert.Equal("test-issuer", token.Issuer);
        Assert.Contains(token.Claims, c => c.Type == "sub");
        Assert.Contains(token.Claims, c => c.Type == "name" && c.Value == "Super Administrator");
        Assert.Contains(token.Claims, c => c.Type == "role" && c.Value == "SuperAdmin");
        Assert.Contains(token.Claims, c => c.Type == "tenantId");
    }

    // --- SEC_P0Triage §Rule 3: weak-password denylist + complexity ---

    [Theory]
    [InlineData("changeme")]
    [InlineData("admin")]
    [InlineData("Admin1234567!")]    // contains "admin"
    [InlineData("ChangeMe123!XYZ")]  // contains "changeme"
    [InlineData("Password123!")]     // contains "password"
    [InlineData("Knowz!Deploy9")]    // contains "knowz"
    [InlineData("LetMeIn1234!")]     // contains "letmein"
    public void IsWeakPassword_Rejects_DenylistSubstrings(string password)
    {
        Assert.True(AuthService.IsWeakPassword(password),
            $"Expected weak-password check to reject '{password}'.");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("short1!A")]              // 8 chars — too short
    [InlineData("nouppercase123!")]       // no uppercase
    [InlineData("NOLOWERCASE123!")]       // no lowercase
    [InlineData("NoDigitsOrSymbols")]     // no digit or symbol (also <12 is fine here; 17 chars)
    [InlineData("NoSymbol12345678")]      // no non-alnum
    public void IsWeakPassword_Rejects_ComplexityFailures(string? password)
    {
        Assert.True(AuthService.IsWeakPassword(password),
            $"Expected weak-password check to reject '{password ?? "<null>"}'.");
    }

    [Theory]
    [InlineData("R4pid!Vault-Seed")]
    [InlineData("Str0ng#Random-Value-2026")]
    [InlineData("Xy!8zQp9mTvR2wL7")]
    public void IsWeakPassword_Accepts_StrongPasswords(string password)
    {
        Assert.False(AuthService.IsWeakPassword(password),
            $"Expected weak-password check to accept '{password}'.");
    }

    [Fact]
    public async Task EnsureSuperAdmin_Throws_WhenPasswordOnDenylist()
    {
        _options.SuperAdminPassword = "changeme";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.EnsureSuperAdminExistsAsync());

        Assert.Contains("fails policy", ex.Message, StringComparison.Ordinal);
        // No DB writes on failure — tenant creation is downstream of the guard.
        Assert.Empty(await _db.Users.ToListAsync());
        Assert.Empty(await _db.Tenants.ToListAsync());
    }

    [Fact]
    public async Task EnsureSuperAdmin_Throws_WhenPasswordEmpty()
    {
        _options.SuperAdminPassword = "";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.EnsureSuperAdminExistsAsync());

        Assert.Contains("SuperAdminPassword is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSuperAdmin_Throws_WhenUsernameEmpty()
    {
        _options.SuperAdminUsername = "";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.EnsureSuperAdminExistsAsync());

        Assert.Contains("SuperAdminUsername is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSuperAdmin_Throws_WhenPasswordFailsComplexity()
    {
        _options.SuperAdminPassword = "nouppercase12!";  // no uppercase

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _authService.EnsureSuperAdminExistsAsync());

        Assert.Contains("fails policy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSuperAdmin_Succeeds_WhenPasswordIsStrong()
    {
        // Covered by Should_CreateSuperAdmin_WhenNoneExists — this is the explicit
        // sibling to the denylist tests for readability.
        await _authService.EnsureSuperAdminExistsAsync();

        var user = await _db.Users.SingleAsync();
        Assert.Equal(UserRole.SuperAdmin, user.Role);
    }
}
