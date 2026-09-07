namespace Knowz.Core.Enums;

/// <summary>
/// Role assigned to a user in the self-hosted deployment.
/// WARNING: These integer values are persisted in the database. NEVER reorder, renumber,
/// or insert new values between existing ones. Changing these breaks existing users' roles
/// silently (no migration error, just wrong permissions). Add new roles at the end only.
/// </summary>
public enum UserRole
{
    /// <summary>Regular user with standard access (default — least privilege).</summary>
    User = 0,

    /// <summary>Tenant-level administration.</summary>
    Admin = 1,

    /// <summary>Full platform administration access.</summary>
    SuperAdmin = 2
}
