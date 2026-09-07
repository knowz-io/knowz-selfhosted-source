using Knowz.Core.Models;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Post-search retrieval quality policies for self-hosted.
/// Applies noise detection, recency boost, relevance floor, and adaptive source count.
/// Mirrors platform pipeline quality improvements at the SearchFacade boundary.
/// WorkGroupID: kc-feat-selfhosted-retrieval-policy-20260413-030000
/// </summary>
public static class SearchResultPolicyProcessor
{
    private const double DefaultDecayLambda = 0.023; // 30-day half-life
    private const double DefaultMaxBoostFraction = 0.15;
    private const double DefaultRelativeFloorRatio = 0.03; // 3% of top score
    private const int DefaultMinResults = 3;

    private static readonly string[] TransactionalTitlePatterns =
    {
        "booking confirmation", "receipt", "invoice", "order confirmation",
        "payment confirmation", "shipping confirmation", "reservation",
        "itinerary", "e-ticket", "boarding pass"
    };

    private static readonly string[] AnalyticalQueryPatterns =
    {
        "infrastructure", "architecture", "engineering", "platform", "pipeline",
        "budget", "strategy", "roadmap", "plan", "design", "system",
        "performance", "deployment", "migration", "security", "authentication"
    };

    private static readonly string[] ExhaustiveIndicators =
    {
        "compare", "contrast", "all of", "every", "everything", "complete picture",
        "comprehensive", "exhaustive", "thorough", "full list",
        "list all", "show me all", "what are all", "show me everything"
    };

    private static readonly string[] SimpleIndicators =
    {
        "what is", "what was", "who is", "who was", "when did", "when was",
        "where is", "where was", "how much", "how many", "count",
        "define", "tell me about"
    };

    private static readonly string[] ExhaustiveIntentPhrases =
    {
        "more sources", "more results", "show me more", "find more",
        "complete picture", "full picture", "comprehensive", "exhaustive",
        "what am i missing", "what else", "anything else", "dig deeper",
        "broader", "wider search", "be thorough", "all of them",
        "compare everything", "show everything", "everything relevant"
    };

    // Synonym dictionary (matches platform SynonymExpansionService)
    private static readonly Dictionary<string, string[]> SynonymMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trip"] = new[] { "travel", "journey", "vacation" },
        ["travel"] = new[] { "trip", "journey", "vacation" },
        ["vacation"] = new[] { "trip", "travel", "holiday" },
        ["meeting"] = new[] { "conference", "session", "gathering" },
        ["document"] = new[] { "file", "report", "paper" },
        ["cost"] = new[] { "expense", "spending", "budget", "price" },
        ["budget"] = new[] { "cost", "expense", "spending", "allocation" },
        ["employee"] = new[] { "staff", "worker", "team member", "personnel" },
        ["hire"] = new[] { "recruit", "onboard", "employ" },
        ["onboarding"] = new[] { "hiring", "recruitment", "orientation" },
        ["enrichment"] = new[] { "processing", "enhancement", "augmentation" },
        ["processing"] = new[] { "enrichment", "pipeline", "workflow" },
        ["search"] = new[] { "find", "lookup", "query", "retrieve" },
        ["summary"] = new[] { "overview", "synopsis", "abstract", "brief" },
        ["analysis"] = new[] { "review", "assessment", "evaluation", "audit" },
        ["performance"] = new[] { "metrics", "speed", "latency", "throughput" },
        ["security"] = new[] { "auth", "authentication", "access control", "protection" },
        ["infrastructure"] = new[] { "platform", "cloud", "hosting", "deployment" },
    };

    /// <summary>
    /// Apply all retrieval quality policies in order.
    /// </summary>
    public static List<SearchResultItem> ApplyPolicies(
        List<SearchResultItem> results,
        string query,
        bool isTemporalQuery = false,
        DateTime? referenceTime = null)
    {
        if (results == null || results.Count == 0) return results ?? new();

        // 1. Noise detection (deprioritize transactional in analytical queries)
        ApplyNoiseDetection(results, query);

        // 2. Recency boost
        ApplyRecencyBoost(results, isTemporalQuery, referenceTime);

        // 3. Re-sort by boosted score
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 4. Relevance floor (skip for temporal)
        if (!isTemporalQuery && results.Count > DefaultMinResults)
        {
            var topScore = results[0].Score;
            if (topScore > 0)
            {
                var floor = topScore * DefaultRelativeFloorRatio;
                var filtered = new List<SearchResultItem>();
                foreach (var r in results)
                {
                    if (filtered.Count < DefaultMinResults || r.Score >= floor)
                        filtered.Add(r);
                }
                results = filtered;
            }
        }

        // 5. Adaptive source count
        var recommendedMax = GetRecommendedSourceCount(query);
        if (results.Count > recommendedMax)
            results = results.Take(recommendedMax).ToList();

        return results;
    }

    public static void ApplyNoiseDetection(List<SearchResultItem> results, string query)
    {
        if (results == null || results.Count == 0 || string.IsNullOrWhiteSpace(query)) return;
        var queryLower = query.ToLowerInvariant();
        if (!AnalyticalQueryPatterns.Any(p => queryLower.Contains(p))) return;

        foreach (var result in results)
        {
            var titleLower = (result.Title ?? "").ToLowerInvariant();
            if (TransactionalTitlePatterns.Any(p => titleLower.Contains(p)))
            {
                result.Score *= 0.5;
            }
        }
    }

    public static void ApplyRecencyBoost(
        List<SearchResultItem> results,
        bool isTemporalQuery = false,
        DateTime? referenceTime = null)
    {
        if (results == null || results.Count == 0) return;
        var now = referenceTime ?? DateTime.UtcNow;
        var boostFraction = isTemporalQuery ? DefaultMaxBoostFraction * 2 : DefaultMaxBoostFraction;
        var lambda = isTemporalQuery ? DefaultDecayLambda * 1.5 : DefaultDecayLambda;

        foreach (var result in results)
        {
            var itemDate = result.UpdatedAt ?? result.CreatedAt;
            var daysOld = Math.Max(0, (now - itemDate).TotalDays);
            var decayFactor = Math.Exp(-lambda * daysOld);
            result.Score *= (1.0 + boostFraction * decayFactor);
        }
    }

    public static int GetRecommendedSourceCount(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 10;
        var lower = query.ToLowerInvariant();
        if (ExhaustiveIndicators.Any(ind => lower.Contains(ind))) return 15;
        if (SimpleIndicators.Any(ind => lower.StartsWith(ind)) && query.Length < 80 && !lower.Contains(" and ")) return 5;
        return 10;
    }

    public static bool DetectExhaustiveIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var lower = query.ToLowerInvariant();
        return ExhaustiveIntentPhrases.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Expand query with domain synonyms. Returns expanded query string.
    /// </summary>
    public static string ExpandQueryWithSynonyms(string query, int maxSynonymsPerTerm = 3, int maxTotalExpanded = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expanded = new List<string>();
        var totalAdded = 0;

        foreach (var word in words)
        {
            if (totalAdded >= maxTotalExpanded) break;
            if (SynonymMap.TryGetValue(word.TrimEnd('.', ',', '?', '!'), out var synonyms))
            {
                var toAdd = synonyms.Take(maxSynonymsPerTerm).Where(s => !query.Contains(s, StringComparison.OrdinalIgnoreCase));
                foreach (var syn in toAdd)
                {
                    if (totalAdded >= maxTotalExpanded) break;
                    expanded.Add(syn);
                    totalAdded++;
                }
            }
        }

        return expanded.Count > 0 ? $"{query} {string.Join(" ", expanded)}" : query;
    }
}
