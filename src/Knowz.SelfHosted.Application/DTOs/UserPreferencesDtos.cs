namespace Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// The current user's preference settings. Null fields mean no preference
/// has been set — clients should fall back to their documented defaults.
/// </summary>
public record UserPreferencesDto(string? TimeZonePreference);

/// <summary>
/// Partial update request for user preferences. Only non-null fields are
/// applied — omit a field to leave it unchanged.
/// </summary>
public record UpdateUserPreferencesRequest(string? TimeZonePreference);
