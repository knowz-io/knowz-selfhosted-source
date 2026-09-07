using System.Collections.Concurrent;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Resolved set of all 6 prompts for a given tenant+user context.
/// </summary>
public record ResolvedPromptSet(
    string SystemPrompt,
    string TitlePrompt,
    string SummarizePrompt,
    string TagsPrompt,
    string DocumentEditorPrompt,
    string NoContextResponse);

/// <summary>
/// Singleton service that resolves prompt templates using the three-tier hierarchy
/// (Platform → Tenant → User) with an in-memory cache.
/// </summary>
public class PromptResolutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromptResolutionService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private sealed record CacheEntry(ResolvedPromptSet Prompts, DateTime ExpiresAt);

    public PromptResolutionService(IServiceScopeFactory scopeFactory, ILogger<PromptResolutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves all 6 prompts for the given tenant and optional user.
    /// </summary>
    public async Task<ResolvedPromptSet> ResolvePromptsAsync(
        Guid tenantId, Guid? userId = null, CancellationToken ct = default)
    {
        var cacheKey = $"{tenantId}:{userId?.ToString() ?? "_"}";

        if (_cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry.Prompts;

        var prompts = await LoadAndMergeAsync(tenantId, userId, ct);
        _cache[cacheKey] = new CacheEntry(prompts, DateTime.UtcNow.Add(CacheTtl));
        return prompts;
    }

    /// <summary>
    /// Resolves a single prompt by key, optionally applying string.Format with the given args.
    /// </summary>
    public async Task<string> ResolvePromptAsync(
        string key, Guid tenantId, Guid? userId = null,
        object[]? formatArgs = null, CancellationToken ct = default)
    {
        var set = await ResolvePromptsAsync(tenantId, userId, ct);
        var raw = GetPromptFromSet(set, key);
        return formatArgs is { Length: > 0 } ? string.Format(raw, formatArgs) : raw;
    }

    public void InvalidateForPlatform()
    {
        _cache.Clear();
        _logger.LogDebug("Prompt cache fully cleared (platform change)");
    }

    public void InvalidateForTenant(Guid tenantId)
    {
        var prefix = $"{tenantId}:";
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
        _logger.LogDebug("Prompt cache cleared for tenant {TenantId} ({Count} entries)", tenantId, keysToRemove.Count);
    }

    public void InvalidateForUser(Guid userId)
    {
        var suffix = $":{userId}";
        var keysToRemove = _cache.Keys.Where(k => k.EndsWith(suffix)).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
        _logger.LogDebug("Prompt cache cleared for user {UserId} ({Count} entries)", userId, keysToRemove.Count);
    }

    private async Task<ResolvedPromptSet> LoadAndMergeAsync(
        Guid tenantId, Guid? userId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

            var templates = await db.PromptTemplates
                .Where(pt =>
                    (pt.Scope == PromptScope.Platform) ||
                    (pt.Scope == PromptScope.Tenant && pt.TenantId == tenantId) ||
                    (pt.Scope == PromptScope.User && pt.UserId == userId && userId != null))
                .AsNoTracking()
                .ToListAsync(ct);

            return MergePrompts(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompt templates from DB — using defaults");
            return GetDefaultPromptSet();
        }
    }

    /// <summary>
    /// Pure merge logic — static for easy unit testing.
    /// Platform base → tenant override/supplement → user supplement.
    /// </summary>
    public static ResolvedPromptSet MergePrompts(List<PromptTemplate> templates)
    {
        var result = new Dictionary<string, string>();

        foreach (var key in PromptKeys.All)
        {
            // Start with platform prompt or fallback to default
            var platform = templates.FirstOrDefault(t => t.PromptKey == key && t.Scope == PromptScope.Platform);
            var text = platform?.TemplateText ?? GetDefaultForKey(key);

            // Apply tenant layer
            var tenant = templates.FirstOrDefault(t => t.PromptKey == key && t.Scope == PromptScope.Tenant);
            if (tenant != null)
            {
                text = tenant.MergeStrategy == PromptMergeStrategy.Override
                    ? tenant.TemplateText
                    : text + "\n\n" + tenant.TemplateText;
            }

            // Apply user layer (always supplement, only for eligible keys)
            var user = templates.FirstOrDefault(t => t.PromptKey == key && t.Scope == PromptScope.User);
            if (user != null && PromptKeys.UserEligible.Contains(key))
            {
                text = text + "\n\n" + user.TemplateText;
            }

            result[key] = text;
        }

        return new ResolvedPromptSet(
            SystemPrompt: result[PromptKeys.SystemPrompt],
            TitlePrompt: result[PromptKeys.TitlePrompt],
            SummarizePrompt: result[PromptKeys.SummarizePrompt],
            TagsPrompt: result[PromptKeys.TagsPrompt],
            DocumentEditorPrompt: result[PromptKeys.DocumentEditorPrompt],
            NoContextResponse: result[PromptKeys.NoContextResponse]);
    }

    public static ResolvedPromptSet GetDefaultPromptSet() => new(
        SystemPrompt: DefaultPrompts.SystemPrompt,
        TitlePrompt: DefaultPrompts.TitlePrompt,
        SummarizePrompt: DefaultPrompts.DetailedSummarizePrompt,
        TagsPrompt: DefaultPrompts.TagsPrompt,
        DocumentEditorPrompt: DefaultPrompts.DocumentEditorPrompt,
        NoContextResponse: DefaultPrompts.NoContextResponse);

    private static string GetDefaultForKey(string key) => key switch
    {
        PromptKeys.SystemPrompt => DefaultPrompts.SystemPrompt,
        PromptKeys.TitlePrompt => DefaultPrompts.TitlePrompt,
        PromptKeys.SummarizePrompt => DefaultPrompts.DetailedSummarizePrompt,
        PromptKeys.TagsPrompt => DefaultPrompts.TagsPrompt,
        PromptKeys.DocumentEditorPrompt => DefaultPrompts.DocumentEditorPrompt,
        PromptKeys.NoContextResponse => DefaultPrompts.NoContextResponse,
        _ => string.Empty
    };

    private static string GetPromptFromSet(ResolvedPromptSet set, string key) => key switch
    {
        PromptKeys.SystemPrompt => set.SystemPrompt,
        PromptKeys.TitlePrompt => set.TitlePrompt,
        PromptKeys.SummarizePrompt => set.SummarizePrompt,
        PromptKeys.TagsPrompt => set.TagsPrompt,
        PromptKeys.DocumentEditorPrompt => set.DocumentEditorPrompt,
        PromptKeys.NoContextResponse => set.NoContextResponse,
        _ => string.Empty
    };
}
