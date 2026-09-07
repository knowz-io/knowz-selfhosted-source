using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// FEAT_SelfHostedTemporalAwareness: Resolves user timezones and formats
/// dates in the user's local timezone for chat context and system prompts.
///
/// Ports the minimal subset of main-platform's ChatTimezoneHelper needed
/// by selfhosted's chat/Q&amp;A pipeline. Deliberately permissive — it must
/// never throw from a chat code path, even with an invalid timezone ID.
///
/// This helper is used by:
/// - <see cref="TemporalPromptBuilder"/> to emit the current-date anchor
///   in system prompts (e.g. "Today is 2026-04-10 in America/New_York")
/// - FormatSourceBlock implementations in <see cref="AzureOpenAIService"/>,
///   <see cref="ContextBuilder"/>, and <see cref="PlatformAIService"/> to
///   render per-source "Created:" / "Updated:" lines in the user's local TZ
/// </summary>
public static class ChatTimezoneHelper
{
    /// <summary>
    /// Fallback timezone when the user has no explicit preference.
    /// Matches the documented fallback in <c>UserPreference.TimeZonePreference</c>.
    /// </summary>
    public const string DefaultFallbackTimeZone = "America/New_York";

    /// <summary>
    /// Resolves a timezone ID from a user preference, falling back to
    /// the default when null/empty or when the stored value is not a
    /// valid IANA timezone ID recognized by the host OS.
    /// </summary>
    public static string ResolveTimeZone(string? preference, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(preference))
            return DefaultFallbackTimeZone;

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(preference);
            return preference;
        }
        catch (TimeZoneNotFoundException ex)
        {
            logger?.LogWarning(ex,
                "Stored timezone preference '{Timezone}' is not a valid IANA ID — falling back to {Fallback}",
                preference, DefaultFallbackTimeZone);
            return DefaultFallbackTimeZone;
        }
        catch (InvalidTimeZoneException ex)
        {
            logger?.LogWarning(ex,
                "Corrupt timezone data for stored preference '{Timezone}' — falling back to {Fallback}",
                preference, DefaultFallbackTimeZone);
            return DefaultFallbackTimeZone;
        }
    }

    /// <summary>
    /// Formats a UTC DateTime as "yyyy-MM-dd" in the user's local timezone.
    /// Returns an empty string if the input is default(DateTime) or
    /// DateTime.MinValue (temporal field not populated by producer).
    /// Falls back to UTC formatting if the timezone ID is invalid.
    /// </summary>
    public static string FormatDateInTimezone(
        DateTime utcDateTime,
        string timezoneId,
        ILogger? logger = null)
    {
        // Suppress unpopulated fields — callers rely on this to avoid
        // "Created: 0001-01-01" pollution in chat context.
        if (utcDateTime == default || utcDateTime == DateTime.MinValue)
            return string.Empty;

        var asUtc = EnsureUtc(utcDateTime);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
            return local.ToString("yyyy-MM-dd");
        }
        catch (TimeZoneNotFoundException ex)
        {
            logger?.LogWarning(ex,
                "Invalid IANA timezone '{Timezone}' — falling back to UTC formatting", timezoneId);
            return asUtc.ToString("yyyy-MM-dd");
        }
        catch (InvalidTimeZoneException ex)
        {
            logger?.LogWarning(ex,
                "Corrupt timezone data for '{Timezone}' — falling back to UTC formatting", timezoneId);
            return asUtc.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>
    /// Returns today's date in the user's timezone as "yyyy-MM-dd".
    /// Used by TemporalPromptBuilder for the system-prompt anchor.
    /// </summary>
    public static string TodayInTimezone(
        DateTime nowUtc,
        string timezoneId,
        ILogger? logger = null)
    {
        return FormatDateInTimezone(nowUtc, timezoneId, logger);
    }

    /// <summary>
    /// Returns true when two UTC timestamps fall on the same calendar day
    /// in the target timezone. Used by FormatSourceBlock implementations
    /// to suppress an "Updated:" line when it matches "Created:" on the
    /// same local day (same-day update suppression, R4 in spec).
    /// </summary>
    public static bool SameLocalDay(
        DateTime utcA,
        DateTime utcB,
        string timezoneId)
    {
        if (utcA == default || utcB == default)
            return false;

        var aUtc = EnsureUtc(utcA);
        var bUtc = EnsureUtc(utcB);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            var aLocal = TimeZoneInfo.ConvertTimeFromUtc(aUtc, tz);
            var bLocal = TimeZoneInfo.ConvertTimeFromUtc(bUtc, tz);
            return aLocal.Year == bLocal.Year
                && aLocal.Month == bLocal.Month
                && aLocal.Day == bLocal.Day;
        }
        catch (Exception)
        {
            // Fall back to UTC comparison. Worst case, an "Updated:" line
            // that could have been suppressed is emitted — not a bug.
            return aUtc.Year == bUtc.Year
                && aUtc.Month == bUtc.Month
                && aUtc.Day == bUtc.Day;
        }
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
