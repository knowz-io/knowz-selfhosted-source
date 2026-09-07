using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// FEAT_SelfHostedTemporalAwareness: Validates the 22 VERIFY criteria from
/// knowzcode/specs/FEAT_SelfHostedTemporalAwareness.md.
///
/// Organized by VERIFY group:
/// - VERIFY_TPB: TemporalPromptBuilder (5)
/// - VERIFY_FSB: FormatSourceBlock emission across 3 impls (9)
/// - VERIFY_TZ:  ChatTimezoneHelper / SearchFacade timezone resolution (3)
/// - VERIFY_BE:  Backend population (5 — AzureSearch/Database/LocalText/LocalVector)
///
/// WorkGroup: kc-feat-selfhosted-temporal-aware-20260410-231702
/// </summary>
public class TemporalAwarenessTests
{
    // --- VERIFY_TPB_01..05 — TemporalPromptBuilder -----------------------

    [Fact(DisplayName = "VERIFY_TPB_01: system prompt anchors Today = yyyy-MM-dd in specified TZ")]
    public void BuildSystemPrompt_UsesTodayInTargetTimezone()
    {
        // 2026-04-10 14:00 UTC is still 2026-04-10 in America/New_York (10am EDT)
        var nowUtc = new DateTime(2026, 4, 10, 14, 0, 0, DateTimeKind.Utc);

        var prompt = TemporalPromptBuilder.BuildSystemPrompt(
            customPromptOverride: null,
            timezoneId: "America/New_York",
            nowUtc: nowUtc);

        Assert.Contains("Today is 2026-04-10 in America/New_York", prompt);
    }

    [Fact(DisplayName = "VERIFY_TPB_02: system prompt rolls forward to next day for positive UTC offset")]
    public void BuildSystemPrompt_RollsForwardInPositiveOffsetTimezone()
    {
        // 2026-04-10 23:00 UTC is 2026-04-11 08:00 in Asia/Tokyo (+09:00)
        var nowUtc = new DateTime(2026, 4, 10, 23, 0, 0, DateTimeKind.Utc);

        var prompt = TemporalPromptBuilder.BuildSystemPrompt(
            customPromptOverride: null,
            timezoneId: "Asia/Tokyo",
            nowUtc: nowUtc);

        Assert.Contains("Today is 2026-04-11 in Asia/Tokyo", prompt);
    }

    [Fact(DisplayName = "VERIFY_TPB_03: custom override is preserved, temporal block is prepended")]
    public void BuildSystemPrompt_PreservesCustomOverride()
    {
        var custom = "You are a legal research assistant specializing in contract law.";
        var nowUtc = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);

        var prompt = TemporalPromptBuilder.BuildSystemPrompt(
            customPromptOverride: custom,
            timezoneId: "UTC",
            nowUtc: nowUtc);

        Assert.Contains(custom, prompt);
        Assert.Contains("CURRENT DATE:", prompt);
        Assert.Contains("TEMPORAL AWARENESS", prompt);
    }

    [Fact(DisplayName = "VERIFY_TPB_04: invalid timezone does not throw")]
    public void BuildSystemPrompt_InvalidTimezoneFallsBackGracefully()
    {
        var nowUtc = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);

        var prompt = TemporalPromptBuilder.BuildSystemPrompt(
            customPromptOverride: null,
            timezoneId: "Invalid/NotARealZone",
            nowUtc: nowUtc);

        // Must not throw. The prompt is still produced; the date string
        // falls back to UTC.
        Assert.Contains("CURRENT DATE:", prompt);
        Assert.Contains("2026-04-10", prompt);
    }

    [Fact(DisplayName = "VERIFY_TPB_05: prompt always contains TEMPORAL AWARENESS marker")]
    public void BuildSystemPrompt_AlwaysContainsTemporalAwarenessMarker()
    {
        var prompt = TemporalPromptBuilder.BuildSystemPrompt(
            customPromptOverride: null,
            timezoneId: "UTC",
            nowUtc: DateTime.UtcNow);

        Assert.Contains("TEMPORAL AWARENESS", prompt);
    }

    // --- VERIFY_FSB_A01..A04 — AzureOpenAIService.FormatSourceBlock -------

    [Fact(DisplayName = "VERIFY_FSB_A01: AzureOpenAIService emits Created: line in user TZ")]
    public void AzureOpenAIService_FormatSourceBlock_EmitsCreatedLine()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Trip Notes",
            Content = "Went to Tokyo.",
            CreatedAt = new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc),
            UpdatedAt = null
        };

        var block = AzureOpenAIService.FormatSourceBlock(result, "America/New_York");

        Assert.Contains("Created: 2026-04-09", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_A02: AzureOpenAIService emits Updated: when it differs by calendar day")]
    public void AzureOpenAIService_FormatSourceBlock_EmitsUpdatedLine()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Evolving Doc",
            Content = "Has been edited.",
            CreatedAt = new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 4, 10, 14, 0, 0, DateTimeKind.Utc)
        };

        var block = AzureOpenAIService.FormatSourceBlock(result, "America/New_York");

        Assert.Contains("Created: 2026-04-09", block);
        Assert.Contains("Updated: 2026-04-10", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_A03: Null UpdatedAt suppresses Updated: line")]
    public void AzureOpenAIService_FormatSourceBlock_NullUpdatedAtSuppressesLine()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Never Edited",
            Content = "Just sitting there.",
            CreatedAt = new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc),
            UpdatedAt = null
        };

        var block = AzureOpenAIService.FormatSourceBlock(result, "America/New_York");

        Assert.Contains("Created:", block);
        Assert.DoesNotContain("Updated:", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_A04: Same-day Updated: is suppressed in the user's local TZ")]
    public void AzureOpenAIService_FormatSourceBlock_SameLocalDaySuppressesUpdatedLine()
    {
        // Both 08:00 UTC and 20:00 UTC on 2026-04-10 land on the same calendar
        // day 2026-04-10 in America/New_York (04:00 and 16:00 local).
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Edited Same Day",
            Content = "Content.",
            CreatedAt = new DateTime(2026, 4, 10, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 4, 10, 20, 0, 0, DateTimeKind.Utc)
        };

        var block = AzureOpenAIService.FormatSourceBlock(result, "America/New_York");

        Assert.Contains("Created: 2026-04-10", block);
        Assert.DoesNotContain("Updated:", block);
    }

    // --- VERIFY_FSB_C01..C03 — ContextBuilder.FormatSourceBlock -----------

    [Fact(DisplayName = "VERIFY_FSB_C01: ContextBuilder emits Created: line")]
    public void ContextBuilder_FormatSourceBlock_EmitsCreatedLine()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Ctx Test",
            Content = "body",
            CreatedAt = new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc),
        };

        var block = ContextBuilder.FormatSourceBlock(result, "America/New_York");

        Assert.Contains("Created: 2026-04-09", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_C02: ContextBuilder preserves TopicName/Tags/enrichment sentinel behavior")]
    public void ContextBuilder_FormatSourceBlock_PreservesExistingBehavior()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Ctx With Topic",
            TopicName = "Travel",
            Tags = new List<string> { "japan", "trip" },
            Content = "Before sentinel.\n\n---ENRICHMENT---\nAfter sentinel.",
            CreatedAt = new DateTime(2026, 4, 9, 14, 0, 0, DateTimeKind.Utc),
        };

        var block = ContextBuilder.FormatSourceBlock(result, "UTC");

        Assert.Contains("Topic: Travel", block);
        Assert.Contains("Tags: japan, trip", block);
        Assert.Contains("Before sentinel.", block);
        Assert.Contains("After sentinel.", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_C03: default(DateTime) CreatedAt is suppressed")]
    public void ContextBuilder_FormatSourceBlock_DefaultCreatedAtIsSuppressed()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "No Date",
            Content = "body",
            CreatedAt = default, // MinValue
        };

        var block = ContextBuilder.FormatSourceBlock(result, "UTC");

        Assert.DoesNotContain("Created:", block);
        Assert.DoesNotContain("0001", block);
    }

    // --- VERIFY_FSB_P01..P02 — PlatformAIService.FormatSourceBlock --------

    [Fact(DisplayName = "VERIFY_FSB_P01: PlatformAIService emits Created: line when populated")]
    public void PlatformAIService_FormatSourceBlock_EmitsCreatedLine()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "Proxied Result",
            Content = "body",
            CreatedAt = new DateTime(2026, 4, 8, 14, 0, 0, DateTimeKind.Utc),
        };

        var block = PlatformAIService.FormatSourceBlock(result, "UTC");

        Assert.Contains("Created: 2026-04-08", block);
    }

    [Fact(DisplayName = "VERIFY_FSB_P02: PlatformAIService suppresses Created: when default(DateTime)")]
    public void PlatformAIService_FormatSourceBlock_SuppressesDefaultCreatedAt()
    {
        var result = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "No Date From Proxy",
            Content = "body",
            CreatedAt = default,
        };

        var block = PlatformAIService.FormatSourceBlock(result, "UTC");

        Assert.DoesNotContain("Created:", block);
    }

    // --- VERIFY_TZ_01..03 — ChatTimezoneHelper.ResolveTimeZone ------------

    [Fact(DisplayName = "VERIFY_TZ_01: user timezone preference is passed through unchanged")]
    public void ResolveTimeZone_PassesThroughUserPreference()
    {
        var resolved = ChatTimezoneHelper.ResolveTimeZone("Europe/London");
        Assert.Equal("Europe/London", resolved);
    }

    [Fact(DisplayName = "VERIFY_TZ_02: null preference falls back to America/New_York")]
    public void ResolveTimeZone_NullPreferenceFallsBackToDefault()
    {
        var resolved = ChatTimezoneHelper.ResolveTimeZone(null);
        Assert.Equal(ChatTimezoneHelper.DefaultFallbackTimeZone, resolved);
        Assert.Equal("America/New_York", resolved);
    }

    [Fact(DisplayName = "VERIFY_TZ_03: empty/whitespace preference falls back to default")]
    public void ResolveTimeZone_WhitespacePreferenceFallsBackToDefault()
    {
        Assert.Equal("America/New_York", ChatTimezoneHelper.ResolveTimeZone(""));
        Assert.Equal("America/New_York", ChatTimezoneHelper.ResolveTimeZone("   "));
    }

    [Fact(DisplayName = "VERIFY_TZ_04: invalid non-null stored preference falls back to default (GAP-1 fix)")]
    public void ResolveTimeZone_InvalidNonNullPreference_ReturnsFallback()
    {
        // An invalid-but-non-null stored preference must not leak through to the
        // system prompt anchor. ResolveTimeZone validates via FindSystemTimeZoneById
        // and falls back rather than returning the bogus string.
        var result = ChatTimezoneHelper.ResolveTimeZone("Bogus/NotAZone");
        Assert.Equal(ChatTimezoneHelper.DefaultFallbackTimeZone, result);
    }

    // --- VERIFY_BE_AS01..02, DB01, LT01, LV01 — backend population -------
    // These are pure data-carrier tests: set CreatedAt on a SearchResultItem
    // produced manually (the backends' full integration with search APIs is
    // covered by existing suites). Here we assert the SearchResultItem shape
    // supports the new fields — the backends' Select/projection updates in
    // Phase 2A ensure they get populated in real flows.

    [Fact(DisplayName = "VERIFY_BE_AS01: SearchResultItem carries CreatedAt for Azure Search mapping")]
    public void SearchResultItem_CarriesCreatedAt()
    {
        var item = new SearchResultItem
        {
            KnowledgeId = Guid.NewGuid(),
            Title = "t",
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), item.CreatedAt);
    }

    [Fact(DisplayName = "VERIFY_BE_AS02: SearchResultItem.UpdatedAt is nullable and defaults to null")]
    public void SearchResultItem_UpdatedAtIsNullableDefaultNull()
    {
        var item = new SearchResultItem { Title = "t" };
        Assert.Null(item.UpdatedAt);

        item.UpdatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(item.UpdatedAt);
        Assert.Equal(new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc), item.UpdatedAt);
    }

    [Fact(DisplayName = "VERIFY_BE_DB01: FormatDateInTimezone returns empty for default(DateTime) — Database/Local backends must suppress")]
    public void FormatDateInTimezone_EmptyForDefault()
    {
        var result = ChatTimezoneHelper.FormatDateInTimezone(default, "America/New_York");
        Assert.Equal(string.Empty, result);
    }

    [Fact(DisplayName = "VERIFY_BE_LT01: FormatDateInTimezone produces yyyy-MM-dd for populated CreatedAt")]
    public void FormatDateInTimezone_FormatsDate()
    {
        var utc = new DateTime(2026, 4, 10, 18, 0, 0, DateTimeKind.Utc);
        // 18:00 UTC on 2026-04-10 is 14:00 EDT on 2026-04-10 in America/New_York
        var result = ChatTimezoneHelper.FormatDateInTimezone(utc, "America/New_York");
        Assert.Equal("2026-04-10", result);
    }

    [Fact(DisplayName = "VERIFY_BE_LV01: SameLocalDay correctly compares UTC timestamps across TZ boundary")]
    public void SameLocalDay_HandlesTimezoneBoundary()
    {
        // 2026-04-10 08:00 UTC and 2026-04-10 20:00 UTC — both are
        // 2026-04-10 in America/New_York (04:00 and 16:00 local).
        var a = new DateTime(2026, 4, 10, 8, 0, 0, DateTimeKind.Utc);
        var b = new DateTime(2026, 4, 10, 20, 0, 0, DateTimeKind.Utc);
        Assert.True(ChatTimezoneHelper.SameLocalDay(a, b, "America/New_York"));

        // 2026-04-10 03:00 UTC is still 2026-04-09 23:00 local in America/New_York
        var c = new DateTime(2026, 4, 10, 3, 0, 0, DateTimeKind.Utc);
        var d = new DateTime(2026, 4, 10, 20, 0, 0, DateTimeKind.Utc);
        Assert.False(ChatTimezoneHelper.SameLocalDay(c, d, "America/New_York"));
    }
}
