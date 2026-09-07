using Knowz.Core.Entities;

namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Per-user preference settings. One record per user, created lazily when
/// the user first saves a preference. If no record exists, defaults apply.
///
/// Kept separate from <see cref="UserPermissions"/> (which governs access
/// control) to keep the schema semantically clean — preferences are
/// cosmetic/UX concerns that should never be confused with authorization.
/// </summary>
public class UserPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// User's preferred IANA timezone identifier (e.g. "America/New_York",
    /// "Europe/London", "Asia/Tokyo"). Null means the user has no
    /// explicit preference — the UI should fall back to its default
    /// (currently "America/New_York") rather than UTC or browser-detected.
    /// </summary>
    public string? TimeZonePreference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual User User { get; set; } = null!;
}
