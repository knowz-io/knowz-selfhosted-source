using Knowz.Core.Models;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Unit tests for <see cref="SearchResultPolicyProcessor"/>.
/// WorkGroupID: kc-feat-selfhosted-retrieval-policy-20260413-030000
/// </summary>
public class SearchResultPolicyProcessorTests
{
    private static SearchResultItem MakeResult(double score, string title = "Test", DateTime? createdAt = null) =>
        new()
        {
            Score = score,
            Title = title,
            KnowledgeId = Guid.NewGuid(),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Content = "test content",
            Tags = new List<string>()
        };

    #region ApplyPolicies

    [Fact]
    public void ApplyPolicies_EmptyList_ReturnsEmpty()
    {
        var result = SearchResultPolicyProcessor.ApplyPolicies(new List<SearchResultItem>(), "query");
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyPolicies_NullList_ReturnsEmpty()
    {
        var result = SearchResultPolicyProcessor.ApplyPolicies(null!, "query");
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyPolicies_SingleItem_ReturnedWithBoost()
    {
        var item = MakeResult(10.0);
        var result = SearchResultPolicyProcessor.ApplyPolicies(new List<SearchResultItem> { item }, "simple query");

        Assert.Single(result);
        // Score should be >= original due to recency boost (item created now)
        Assert.True(result[0].Score >= 10.0);
    }

    [Fact]
    public void ApplyPolicies_MultipleItems_SortedByScoreDescending()
    {
        var items = new List<SearchResultItem>
        {
            MakeResult(5.0, "Low"),
            MakeResult(20.0, "High"),
            MakeResult(10.0, "Mid")
        };

        var result = SearchResultPolicyProcessor.ApplyPolicies(items, "some query");

        Assert.True(result[0].Score >= result[1].Score, "First item should have highest score");
        if (result.Count > 2)
            Assert.True(result[1].Score >= result[2].Score, "Second item should have higher score than third");
    }

    #endregion

    #region ApplyNoiseDetection

    [Fact]
    public void ApplyNoiseDetection_NonAnalyticalQuery_NoScoreChanges()
    {
        var items = new List<SearchResultItem>
        {
            MakeResult(10.0, "Booking Confirmation for Hotel"),
            MakeResult(10.0, "Regular Document")
        };

        SearchResultPolicyProcessor.ApplyNoiseDetection(items, "what did I have for lunch");

        Assert.Equal(10.0, items[0].Score);
        Assert.Equal(10.0, items[1].Score);
    }

    [Fact]
    public void ApplyNoiseDetection_AnalyticalQuery_BookingTitle_ScoreHalved()
    {
        var item = MakeResult(10.0, "Booking Confirmation for Flight");
        var items = new List<SearchResultItem> { item };

        SearchResultPolicyProcessor.ApplyNoiseDetection(items, "what is the infrastructure cost");

        Assert.Equal(5.0, item.Score, precision: 5);
    }

    [Fact]
    public void ApplyNoiseDetection_AnalyticalQuery_NormalTitle_ScoreUnchanged()
    {
        var item = MakeResult(10.0, "Engineering Architecture Doc");
        var items = new List<SearchResultItem> { item };

        SearchResultPolicyProcessor.ApplyNoiseDetection(items, "what is the infrastructure cost");

        Assert.Equal(10.0, item.Score, precision: 5);
    }

    [Fact]
    public void ApplyNoiseDetection_NullOrEmptyInputs_NoException()
    {
        SearchResultPolicyProcessor.ApplyNoiseDetection(null!, "query");
        SearchResultPolicyProcessor.ApplyNoiseDetection(new List<SearchResultItem>(), "query");
        SearchResultPolicyProcessor.ApplyNoiseDetection(new List<SearchResultItem> { MakeResult(10.0) }, "");
        SearchResultPolicyProcessor.ApplyNoiseDetection(new List<SearchResultItem> { MakeResult(10.0) }, null!);
    }

    #endregion

    #region ApplyRecencyBoost

    [Fact]
    public void ApplyRecencyBoost_RecentItem_GetsHigherBoostThanOldItem()
    {
        var now = DateTime.UtcNow;
        var recentItem = MakeResult(10.0, "Recent", now);
        var oldItem = MakeResult(10.0, "Old", now.AddDays(-90));

        var items = new List<SearchResultItem> { recentItem, oldItem };
        SearchResultPolicyProcessor.ApplyRecencyBoost(items, isTemporalQuery: false, referenceTime: now);

        Assert.True(recentItem.Score > oldItem.Score,
            $"Recent item ({recentItem.Score:F4}) should score higher than old item ({oldItem.Score:F4})");
    }

    [Fact]
    public void ApplyRecencyBoost_TemporalQuery_StrongerBoost()
    {
        var now = DateTime.UtcNow;
        var normalItem = MakeResult(10.0, "Normal", now);
        var temporalItem = MakeResult(10.0, "Temporal", now);

        SearchResultPolicyProcessor.ApplyRecencyBoost(
            new List<SearchResultItem> { normalItem }, isTemporalQuery: false, referenceTime: now);
        SearchResultPolicyProcessor.ApplyRecencyBoost(
            new List<SearchResultItem> { temporalItem }, isTemporalQuery: true, referenceTime: now);

        // Temporal query uses 2x boostFraction, so boost should be larger
        Assert.True(temporalItem.Score > normalItem.Score,
            $"Temporal boost ({temporalItem.Score:F4}) should exceed normal boost ({normalItem.Score:F4})");
    }

    [Fact]
    public void ApplyRecencyBoost_AllItemsBoosted_ScoresIncrease()
    {
        var now = DateTime.UtcNow;
        var item = MakeResult(10.0, "Test", now.AddDays(-30));
        var originalScore = item.Score;

        SearchResultPolicyProcessor.ApplyRecencyBoost(
            new List<SearchResultItem> { item }, isTemporalQuery: false, referenceTime: now);

        Assert.True(item.Score > originalScore,
            "Score should increase after recency boost");
    }

    #endregion

    #region GetRecommendedSourceCount

    [Fact]
    public void GetRecommendedSourceCount_SimpleQuestion_HowMany_Returns5()
    {
        var count = SearchResultPolicyProcessor.GetRecommendedSourceCount("how many items?");
        Assert.Equal(5, count);
    }

    [Fact]
    public void GetRecommendedSourceCount_ComplexQuestion_CompareAll_Returns15()
    {
        var count = SearchResultPolicyProcessor.GetRecommendedSourceCount("compare all engineering work");
        Assert.Equal(15, count);
    }

    [Fact]
    public void GetRecommendedSourceCount_SimpleIndicator_TellMeAbout_Returns5()
    {
        var count = SearchResultPolicyProcessor.GetRecommendedSourceCount("tell me about X");
        Assert.Equal(5, count);
    }

    [Fact]
    public void GetRecommendedSourceCount_RegularQuery_Returns10()
    {
        var count = SearchResultPolicyProcessor.GetRecommendedSourceCount("some regular query about things");
        Assert.Equal(10, count);
    }

    [Fact]
    public void GetRecommendedSourceCount_NullOrEmpty_Returns10()
    {
        Assert.Equal(10, SearchResultPolicyProcessor.GetRecommendedSourceCount(null!));
        Assert.Equal(10, SearchResultPolicyProcessor.GetRecommendedSourceCount(""));
        Assert.Equal(10, SearchResultPolicyProcessor.GetRecommendedSourceCount("   "));
    }

    [Fact]
    public void GetRecommendedSourceCount_ExhaustiveOverridesSimple()
    {
        // "list all" is exhaustive; even though "how many" is simple, exhaustive wins
        // because exhaustive is checked first
        var count = SearchResultPolicyProcessor.GetRecommendedSourceCount("list all the things");
        Assert.Equal(15, count);
    }

    #endregion

    #region DetectExhaustiveIntent

    [Fact]
    public void DetectExhaustiveIntent_MoreSources_ReturnsTrue()
    {
        Assert.True(SearchResultPolicyProcessor.DetectExhaustiveIntent("show me more sources"));
    }

    [Fact]
    public void DetectExhaustiveIntent_WhatAmIMissing_ReturnsTrue()
    {
        Assert.True(SearchResultPolicyProcessor.DetectExhaustiveIntent("what am i missing"));
    }

    [Fact]
    public void DetectExhaustiveIntent_RegularQuestion_ReturnsFalse()
    {
        Assert.False(SearchResultPolicyProcessor.DetectExhaustiveIntent("regular question"));
    }

    [Fact]
    public void DetectExhaustiveIntent_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(SearchResultPolicyProcessor.DetectExhaustiveIntent(null!));
        Assert.False(SearchResultPolicyProcessor.DetectExhaustiveIntent(""));
        Assert.False(SearchResultPolicyProcessor.DetectExhaustiveIntent("   "));
    }

    [Fact]
    public void DetectExhaustiveIntent_ComprehensiveQuery_ReturnsTrue()
    {
        Assert.True(SearchResultPolicyProcessor.DetectExhaustiveIntent("give me a comprehensive overview"));
    }

    #endregion

    #region ExpandQueryWithSynonyms

    [Fact]
    public void ExpandQueryWithSynonyms_InfrastructureCosts_IncludesPlatformOrCloud()
    {
        var expanded = SearchResultPolicyProcessor.ExpandQueryWithSynonyms("infrastructure costs");

        Assert.Contains("infrastructure costs", expanded);
        // "infrastructure" maps to ["platform", "cloud", "hosting", "deployment"]
        // "cost" maps to ["expense", "spending", "budget", "price"]
        Assert.True(
            expanded.Contains("platform") || expanded.Contains("cloud"),
            $"Expected synonyms for 'infrastructure' but got: {expanded}");
    }

    [Fact]
    public void ExpandQueryWithSynonyms_HiringProcess_IncludesRecruitOrOnboard()
    {
        var expanded = SearchResultPolicyProcessor.ExpandQueryWithSynonyms("hire process");

        Assert.Contains("hire process", expanded);
        Assert.True(
            expanded.Contains("recruit") || expanded.Contains("onboard"),
            $"Expected synonyms for 'hire' but got: {expanded}");
    }

    [Fact]
    public void ExpandQueryWithSynonyms_UnknownWords_ReturnsUnchanged()
    {
        var query = "unknown word query";
        var expanded = SearchResultPolicyProcessor.ExpandQueryWithSynonyms(query);

        Assert.Equal(query, expanded);
    }

    [Fact]
    public void ExpandQueryWithSynonyms_NullOrEmpty_ReturnsInput()
    {
        Assert.Null(SearchResultPolicyProcessor.ExpandQueryWithSynonyms(null!));
        Assert.Equal("", SearchResultPolicyProcessor.ExpandQueryWithSynonyms(""));
        Assert.Equal("   ", SearchResultPolicyProcessor.ExpandQueryWithSynonyms("   "));
    }

    [Fact]
    public void ExpandQueryWithSynonyms_DoesNotDuplicateExistingTerms()
    {
        // "trip" synonyms include "travel", but "travel" is already in the query
        var expanded = SearchResultPolicyProcessor.ExpandQueryWithSynonyms("trip travel plans");

        // Count occurrences of "travel" - should appear only once (original)
        var travelCount = expanded.Split(' ').Count(w => w.Equals("travel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, travelCount);
    }

    #endregion

    #region RelevanceFloor (via ApplyPolicies)

    [Fact]
    public void ApplyPolicies_RelevanceFloor_DropsItemsBelowThreePercentOfTop()
    {
        // Use a fixed reference time and old dates so recency boost is minimal/uniform
        var referenceTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldDate = referenceTime.AddDays(-365);

        var items = new List<SearchResultItem>
        {
            MakeResult(100.0, "Top", oldDate),
            MakeResult(50.0, "Mid", oldDate),
            MakeResult(20.0, "Low", oldDate),
            MakeResult(1.0, "Noise", oldDate)
        };

        var result = SearchResultPolicyProcessor.ApplyPolicies(
            items, "some regular query about things", isTemporalQuery: false, referenceTime: referenceTime);

        // The noise item (score ~1) should be below the 3% floor of the top score (~100)
        // Floor ~ 3.0, so item with score ~1 should be dropped
        // Items with scores 100, 50, 20 should survive (all > 3% of top)
        var titles = result.Select(r => r.Title).ToList();
        Assert.Contains("Top", titles);
        Assert.Contains("Mid", titles);
        Assert.Contains("Low", titles);
        Assert.DoesNotContain("Noise", titles);
    }

    [Fact]
    public void ApplyPolicies_TemporalQuery_NoRelevanceFloorFiltering()
    {
        var referenceTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldDate = referenceTime.AddDays(-365);

        var items = new List<SearchResultItem>
        {
            MakeResult(100.0, "Top", oldDate),
            MakeResult(50.0, "Mid", oldDate),
            MakeResult(20.0, "Low", oldDate),
            MakeResult(1.0, "Noise", oldDate)
        };

        var result = SearchResultPolicyProcessor.ApplyPolicies(
            items, "some regular query about things", isTemporalQuery: true, referenceTime: referenceTime);

        // Temporal queries skip relevance floor — all items should survive
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ApplyPolicies_FewerThanMinResults_AllKept()
    {
        var referenceTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldDate = referenceTime.AddDays(-365);

        // Only 2 items, both would normally fail the floor but minResults=3 protects them
        var items = new List<SearchResultItem>
        {
            MakeResult(100.0, "Top", oldDate),
            MakeResult(0.5, "Tiny", oldDate)
        };

        var result = SearchResultPolicyProcessor.ApplyPolicies(
            items, "some regular query", isTemporalQuery: false, referenceTime: referenceTime);

        // With only 2 items (< DefaultMinResults=3), relevance floor is skipped entirely
        Assert.Equal(2, result.Count);
    }

    #endregion
}
