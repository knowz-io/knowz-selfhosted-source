using Knowz.SelfHosted.Application.DTOs;

namespace Knowz.SelfHosted.Application.Interfaces;

/// <summary>
/// Reads and writes per-user preference settings (timezone, etc.).
/// Preferences are cosmetic/UX concerns, distinct from authorization.
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>
    /// Returns the current preferences for the given user, or an
    /// all-null record if no preferences have been saved yet.
    /// </summary>
    Task<UserPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Upserts the given user's preferences. Only non-null fields in the
    /// request are applied — null fields leave the existing value unchanged.
    /// Returns the full post-update preference record.
    /// </summary>
    Task<UserPreferencesDto> UpdateAsync(
        Guid userId,
        Guid tenantId,
        UpdateUserPreferencesRequest request,
        CancellationToken ct = default);
}
