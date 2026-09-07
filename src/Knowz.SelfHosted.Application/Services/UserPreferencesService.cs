using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Knowz.SelfHosted.Application.Services;

/// <inheritdoc />
public class UserPreferencesService : IUserPreferencesService
{
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<UserPreferencesService> _logger;

    // IANA timezone names are alphanumerics + `_`, `/`, `+`, `-`. Cap at 100 chars
    // to match the column size. This is a format check, not an existence check —
    // see `TimeZoneInfo.FindSystemTimeZoneById` for the authoritative validation
    // performed downstream when formatters consume the value.
    private static readonly Regex IanaNamePattern =
        new(@"^[A-Za-z_]+(?:/[A-Za-z_+\-0-9]+)*$", RegexOptions.Compiled);

    public UserPreferencesService(SelfHostedDbContext db, ILogger<UserPreferencesService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<UserPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await _db.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct)
            .ConfigureAwait(false);

        return new UserPreferencesDto(TimeZonePreference: pref?.TimeZonePreference);
    }

    public async Task<UserPreferencesDto> UpdateAsync(
        Guid userId,
        Guid tenantId,
        UpdateUserPreferencesRequest request,
        CancellationToken ct = default)
    {
        // Validate timezone format before touching the DB. An empty-string
        // value is interpreted as "clear the preference".
        string? normalizedTz = null;
        if (request.TimeZonePreference is not null)
        {
            var trimmed = request.TimeZonePreference.Trim();
            if (trimmed.Length == 0)
            {
                normalizedTz = null;
            }
            else
            {
                if (trimmed.Length > 100)
                    throw new ArgumentException("TimeZonePreference must be 100 characters or fewer.");
                if (!IanaNamePattern.IsMatch(trimmed))
                    throw new ArgumentException("TimeZonePreference must be a valid IANA timezone identifier (e.g. 'America/New_York').");

                // Validate against the OS timezone database. Throws if unknown.
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
                }
                catch (TimeZoneNotFoundException)
                {
                    throw new ArgumentException($"Unknown timezone '{trimmed}'.");
                }
                catch (InvalidTimeZoneException)
                {
                    throw new ArgumentException($"Invalid timezone '{trimmed}'.");
                }

                normalizedTz = trimmed;
            }
        }

        var pref = await _db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (pref is null)
        {
            pref = new UserPreference
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                TimeZonePreference = normalizedTz,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.UserPreferences.Add(pref);
        }
        else
        {
            // Only overwrite fields that were included in the request (we
            // currently only have one, but leave the shape so future fields
            // can partial-update cleanly).
            if (request.TimeZonePreference is not null)
                pref.TimeZonePreference = normalizedTz;

            pref.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Updated preferences for user {UserId}: timeZone={TimeZone}",
            userId, pref.TimeZonePreference ?? "<unset>");

        return new UserPreferencesDto(TimeZonePreference: pref.TimeZonePreference);
    }
}
