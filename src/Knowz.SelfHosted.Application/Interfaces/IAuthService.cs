using Knowz.SelfHosted.Application.Models;

namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// Handles authentication: login, API key validation, JWT generation, password hashing, and SuperAdmin seeding.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Authenticates a user by username and password.
    /// </summary>
    Task<AuthResult> LoginAsync(string username, string password);

    /// <summary>
    /// Validates an API key and returns an AuthResult if valid.
    /// </summary>
    Task<AuthResult?> ValidateApiKeyAsync(string apiKey);

    /// <summary>
    /// Gets a user DTO by user ID.
    /// </summary>
    Task<UserDto?> GetCurrentUserAsync(Guid userId);

    /// <summary>
    /// Ensures a SuperAdmin user exists. Called at application startup.
    /// Creates one using SelfHostedOptions defaults if none exists.
    /// </summary>
    Task EnsureSuperAdminExistsAsync();

    /// <summary>
    /// Hashes a plaintext password using BCrypt.
    /// </summary>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a plaintext password against a BCrypt hash.
    /// </summary>
    bool VerifyPassword(string password, string hash);

    /// <summary>Returns true when a password satisfies the shared administrator policy.</summary>
    bool IsPasswordStrong(string password);

    /// <summary>
    /// Multi-tenant login: validates credentials and returns tenant selection info if the user belongs to multiple tenants.
    /// </summary>
    Task<MultiTenantLoginResult> MultiTenantLoginAsync(string username, string password);

    /// <summary>
    /// Selects a tenant after a multi-tenant login.
    /// Requires a valid single-use selection token from the login response.
    /// </summary>
    Task<AuthResult> SelectTenantAsync(Guid userId, Guid tenantId, string? selectionToken = null);

    /// <summary>
    /// Switches the authenticated user's active tenant context.
    /// </summary>
    Task<AuthResult> SwitchTenantAsync(Guid userId, Guid newTenantId);

    /// <summary>
    /// Gets all tenant memberships for a user.
    /// </summary>
    Task<List<TenantMembershipDto>> GetUserTenantsAsync(Guid userId);

    /// <summary>
    /// Issue a bootstrap API key for the seeded SuperAdmin.
    /// SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §2.3 — returns the plaintext of a
    /// newly-minted <c>ksh_*</c> key stored on the SuperAdmin user. Idempotent:
    /// if the SuperAdmin already has an ApiKey set, returns null (the caller
    /// treats this as "nothing to write").
    /// </summary>
    /// <returns>The plaintext API key, or null if one already exists.</returns>
    Task<string?> IssueBootstrapApiKeyForSuperAdminAsync();
}
