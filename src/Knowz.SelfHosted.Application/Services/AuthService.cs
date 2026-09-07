using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Application.Extensions;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Models;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Handles authentication: login, API key validation, JWT generation, password hashing, and SuperAdmin seeding.
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>
    /// Short-lived, single-use tokens for tenant selection after multi-tenant login.
    /// Prevents unauthenticated callers from selecting a tenant with just a userId.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, (string Token, DateTime Expiry)> _tenantSelectionTokens = new();

    /// <summary>
    /// Denylist of fragments that MUST NOT appear (case-insensitive substring match)
    /// in the SuperAdmin seed password. Seeded with the "top-N" common credentials
    /// enterprise deployers have historically left in place, plus project-specific
    /// values a rushed operator might type. Refresh quarterly against HIBP — tracked
    /// in SEC_P0Triage debt item SEC_WeakPasswordListRefresh.
    /// </summary>
    internal static readonly string[] WeakPasswordList =
    {
        "admin", "changeme", "password", "p@ssw0rd", "p@ssword",
        "letmein", "welcome", "knowz", "selfhosted", "default",
        "root", "qwerty", "abc123", "iloveyou", "monkey",
        "dragon", "master", "superuser", "administrator",
        "dev-fallback-secret-key", // the literal removed in Item 4
    };

    /// <summary>
    /// Complexity policy (SEC_P0Triage §Rule 3): &gt;=12 chars with at least one
    /// uppercase, one lowercase, one digit, and one non-alphanumeric.
    /// </summary>
    private static readonly Regex PasswordComplexityRegex = new(
        @"^(?=.{12,})(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns true when <paramref name="password"/> fails the weak-password policy
    /// (empty/null, matches denylist substring case-insensitively, or fails
    /// complexity regex). Internal so tests can assert policy decisions directly.
    /// </summary>
    internal static bool IsWeakPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return true;

        foreach (var fragment in WeakPasswordList)
        {
            if (password.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !PasswordComplexityRegex.IsMatch(password);
    }

    private readonly SelfHostedDbContext _db;
    private readonly SelfHostedOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        SelfHostedDbContext db,
        IOptions<SelfHostedOptions> options,
        ILogger<AuthService> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AuthResult> LoginAsync(string username, string password)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Username == username);

        if (user is null)
        {
            _logger.LogWarning("Login attempt for non-existent user: {Username}", username);
            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt for inactive user: {Username}", username);
            throw new UnauthorizedAccessException("User account is inactive.");
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            _logger.LogWarning("Invalid password for user: {Username}", username);
            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        // Update last login time
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return GenerateAuthResult(user);
    }

    public async Task<AuthResult?> ValidateApiKeyAsync(string apiKey)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.ApiKey == apiKey);

        if (user is null || !user.IsActive)
        {
            return null;
        }

        return GenerateAuthResult(user);
    }

    public async Task<UserDto?> GetCurrentUserAsync(Guid userId)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null)
        {
            return null;
        }

        var timeZonePreference = await _db.UserPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.TimeZonePreference)
            .FirstOrDefaultAsync();

        return user.ToDto(timeZonePreference);
    }

    public async Task EnsureSuperAdminExistsAsync()
    {
        var hasSuperAdmin = await _db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin);
        if (hasSuperAdmin)
        {
            _logger.LogInformation("SuperAdmin user already exists. Skipping seed.");
            return;
        }

        // SEC_P0Triage §Rule 3: refuse to seed with weak/guessable credentials.
        // Validate BEFORE any DB writes (tenant creation) so a misconfigured
        // deploy crashes at boot rather than half-creating records.
        if (string.IsNullOrWhiteSpace(_options.SuperAdminUsername))
        {
            throw new InvalidOperationException(
                "SelfHosted:SuperAdminUsername is required at first boot. " +
                "Supply via env var SelfHosted__SuperAdminUsername or KV secret " +
                "SelfHosted--SuperAdmin--Username.");
        }

        if (string.IsNullOrWhiteSpace(_options.SuperAdminPassword))
        {
            throw new InvalidOperationException(
                "SelfHosted:SuperAdminPassword is required at first boot. " +
                "Supply via env var SelfHosted__SuperAdminPassword or KV secret " +
                "SelfHosted--SuperAdmin--Password.");
        }

        if (IsWeakPassword(_options.SuperAdminPassword))
        {
            throw new InvalidOperationException(
                "SelfHosted:SuperAdminPassword fails policy — " +
                "must be >=12 chars, include upper/lower/digit/non-alphanumeric, " +
                "and must not contain common fragments (admin, changeme, password, ...). " +
                "Rotate via KV secret SelfHosted--SuperAdmin--Password.");
        }

        // Check if admin exists with wrong role (legacy enum values from before SwapUserRoleValues)
        var existingAdmin = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == _options.SuperAdminUsername);
        if (existingAdmin != null)
        {
            _logger.LogWarning(
                "User '{Username}' exists with Role={Role} but no SuperAdmin found. Fixing role.",
                existingAdmin.Username, existingAdmin.Role);
            existingAdmin.Role = UserRole.SuperAdmin;
            await _db.SaveChangesAsync();
            return;
        }

        _logger.LogInformation("No SuperAdmin found. Creating default SuperAdmin user.");

        // Create or find default tenant
        var defaultTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == "default");
        if (defaultTenant is null)
        {
            defaultTenant = new Tenant
            {
                Name = "Default",
                Slug = "default",
                Description = "Default tenant created during initial setup",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Tenants.Add(defaultTenant);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Created default tenant: {TenantId}", defaultTenant.Id);
        }

        var superAdmin = new User
        {
            TenantId = defaultTenant.Id,
            Username = _options.SuperAdminUsername,
            PasswordHash = HashPassword(_options.SuperAdminPassword),
            DisplayName = "Super Administrator",
            Role = UserRole.SuperAdmin,
            IsActive = true,
            MustChangePassword = _options.RequireSuperAdminPasswordChange,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(superAdmin);
        await _db.SaveChangesAsync();

        // Create tenant membership for the SuperAdmin
        var membership = new UserTenantMembership
        {
            UserId = superAdmin.Id,
            TenantId = defaultTenant.Id,
            Role = UserRole.SuperAdmin,
            IsActive = true,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserTenantMemberships.Add(membership);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created SuperAdmin user: {Username} in tenant: {TenantName}",
            superAdmin.Username, defaultTenant.Name);
    }

    public async Task<string?> IssueBootstrapApiKeyForSuperAdminAsync()
    {
        // SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §Rule 2: idempotent — if a key already
        // exists on the SuperAdmin row, we do NOT mint a new one. The bootstrap
        // key is single-issue; rotation is an explicit operator action.
        var superAdmin = await _db.Users
            .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
        if (superAdmin is null)
        {
            _logger.LogWarning("IssueBootstrapApiKeyForSuperAdminAsync: no SuperAdmin row found.");
            return null;
        }
        if (!string.IsNullOrWhiteSpace(superAdmin.ApiKey))
        {
            return null; // already issued
        }

        // Format: ksh_ + 32 url-safe random chars. Short name, unambiguous prefix.
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var plaintext = "ksh_" + Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        superAdmin.ApiKey = plaintext;
        superAdmin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Intentionally do NOT log the plaintext — caller is responsible for
        // writing it to Key Vault, not stdout / telemetry.
        _logger.LogInformation("Bootstrap API key issued for SuperAdmin {UserId}.", superAdmin.Id);
        return plaintext;
    }

    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }

    public bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }

    public bool IsPasswordStrong(string password) => !IsWeakPassword(password);

    public async Task<MultiTenantLoginResult> MultiTenantLoginAsync(string username, string password)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Username == username);

        if (user is null)
        {
            _logger.LogWarning("Login attempt for non-existent user: {Username}", username);
            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt for inactive user: {Username}", username);
            throw new UnauthorizedAccessException("User account is inactive.");
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            _logger.LogWarning("Invalid password for user: {Username}", username);
            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        // Update last login time
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Check tenant memberships
        var memberships = await _db.UserTenantMemberships
            .Where(m => m.UserId == user.Id && m.IsActive)
            .Include(m => m.Tenant)
            .ToListAsync();

        // 0 memberships (pre-migration legacy user): fall back to User.TenantId + User.Role
        if (memberships.Count == 0)
        {
            _logger.LogInformation("User {Username} has no tenant memberships, using legacy home tenant", username);
            var authResult = GenerateAuthResult(user);
            return new MultiTenantLoginResult
            {
                Token = authResult.Token,
                ExpiresAt = authResult.ExpiresAt,
                User = authResult.User,
                RequiresTenantSelection = false,
                UserId = user.Id
            };
        }

        // 1 active membership: auto-select
        if (memberships.Count == 1)
        {
            var membership = memberships[0];
            _logger.LogInformation("User {Username} has single tenant membership, auto-selecting tenant {TenantId}", username, membership.TenantId);
            var authResult = GenerateAuthResultForMembership(user, membership.TenantId, membership.Tenant.Name, membership.Role);
            return new MultiTenantLoginResult
            {
                Token = authResult.Token,
                ExpiresAt = authResult.ExpiresAt,
                User = authResult.User,
                RequiresTenantSelection = false,
                UserId = user.Id
            };
        }

        // 2+ active memberships: require tenant selection
        _logger.LogInformation("User {Username} has {Count} tenant memberships, requiring selection", username, memberships.Count);

        // Issue a short-lived, single-use token to prove this caller authenticated
        var selectionToken = Guid.NewGuid().ToString("N");
        _tenantSelectionTokens[user.Id] = (selectionToken, DateTime.UtcNow.AddMinutes(5));

        return new MultiTenantLoginResult
        {
            Token = string.Empty,
            ExpiresAt = null,
            User = null,
            RequiresTenantSelection = true,
            UserId = user.Id,
            SelectionToken = selectionToken,
            AvailableTenants = memberships.Select(m => new TenantMembershipDto
            {
                TenantId = m.TenantId,
                TenantName = m.Tenant.Name,
                TenantSlug = m.Tenant.Slug,
                Role = m.Role,
                IsActive = m.IsActive
            }).ToList()
        };
    }

    public async Task<AuthResult> SelectTenantAsync(Guid userId, Guid tenantId, string? selectionToken = null)
    {
        // Validate the single-use selection token issued during login
        if (!ValidateAndConsumeSelectionToken(userId, selectionToken))
        {
            throw new UnauthorizedAccessException("Invalid or expired selection token.");
        }

        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedAccessException("User not found or inactive.");
        }

        var membership = await _db.UserTenantMemberships
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsActive);

        if (membership is null)
        {
            throw new UnauthorizedAccessException($"User does not have an active membership in the requested tenant.");
        }

        // Update last login time
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return GenerateAuthResultForMembership(user, membership.TenantId, membership.Tenant.Name, membership.Role);
    }

    private static bool ValidateAndConsumeSelectionToken(Guid userId, string? selectionToken)
    {
        if (string.IsNullOrEmpty(selectionToken))
            return false;

        if (!_tenantSelectionTokens.TryRemove(userId, out var stored))
            return false;

        if (stored.Expiry < DateTime.UtcNow)
            return false;

        return string.Equals(stored.Token, selectionToken, StringComparison.Ordinal);
    }

    public async Task<AuthResult> SwitchTenantAsync(Guid userId, Guid newTenantId)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedAccessException("User not found or inactive.");
        }

        var membership = await _db.UserTenantMemberships
            .Include(m => m.Tenant)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == newTenantId && m.IsActive);

        if (membership is null)
        {
            throw new UnauthorizedAccessException($"User does not have an active membership in the requested tenant.");
        }

        return GenerateAuthResultForMembership(user, membership.TenantId, membership.Tenant.Name, membership.Role);
    }

    public async Task<List<TenantMembershipDto>> GetUserTenantsAsync(Guid userId)
    {
        var memberships = await _db.UserTenantMemberships
            .Where(m => m.UserId == userId && m.IsActive)
            .Include(m => m.Tenant)
            .ToListAsync();

        return memberships.Select(m => new TenantMembershipDto
        {
            TenantId = m.TenantId,
            TenantName = m.Tenant.Name,
            TenantSlug = m.Tenant.Slug,
            Role = m.Role,
            IsActive = m.IsActive
        }).ToList();
    }

    private AuthResult GenerateAuthResult(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.JwtExpirationMinutes);
        var token = GenerateJwtToken(user, expiresAt);

        var timeZonePreference = LookupTimeZonePreference(user.Id);

        return new AuthResult
        {
            Token = token,
            ExpiresAt = expiresAt,
            User = user.ToDto(timeZonePreference)
        };
    }

    private AuthResult GenerateAuthResultForMembership(User user, Guid tenantId, string tenantName, UserRole role)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.JwtExpirationMinutes);
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        var token = JwtTokenHelper.GenerateToken(
            user.Id, displayName, tenantId, role, expiresAt,
            _options.JwtSecret, _options.JwtIssuer, _logger,
            user.MustChangePassword);

        var timeZonePreference = LookupTimeZonePreference(user.Id);

        return new AuthResult
        {
            Token = token,
            ExpiresAt = expiresAt,
            User = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                DisplayName = user.DisplayName,
                Role = role,
                TenantId = tenantId,
                TenantName = tenantName,
                IsActive = user.IsActive,
                MustChangePassword = user.MustChangePassword,
                ApiKey = user.ApiKey,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                TimeZonePreference = timeZonePreference
            }
        };
    }

    /// <summary>
    /// Synchronously loads the user's timezone preference. Runs inside
    /// sync auth-result builders, so we use a blocking query. The
    /// UserPreferences table is tiny (one row per user) and reads are
    /// indexed by UserId — this adds ~1ms to login, acceptable.
    /// Returns null if the user has no saved preference.
    /// </summary>
    private string? LookupTimeZonePreference(Guid userId)
    {
        try
        {
            return _db.UserPreferences
                .AsNoTracking()
                .Where(p => p.UserId == userId)
                .Select(p => p.TimeZonePreference)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Never let a preference-read failure break login.
            _logger.LogWarning(ex, "Failed to load timezone preference for user {UserId}", userId);
            return null;
        }
    }

    private string GenerateJwtToken(User user, DateTime expiresAt) =>
        JwtTokenHelper.GenerateToken(user, expiresAt, _options.JwtSecret, _options.JwtIssuer, _logger);

}
