using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public class PromptSeedingService
{
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<PromptSeedingService> _logger;

    public PromptSeedingService(SelfHostedDbContext db, ILogger<PromptSeedingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Seeds the 6 platform-level default prompts on first run, and updates any
    /// system-seeded rows whose canonical text has changed (e.g. prompt upgrades).
    /// Tenant/user customizations (IsSystemSeeded=false) are never touched.
    /// </summary>
    public async Task SeedDefaultPromptsAsync(CancellationToken ct = default)
    {
        var defaults = new Dictionary<string, string>
        {
            [PromptKeys.SystemPrompt] = DefaultPrompts.SystemPrompt,
            [PromptKeys.TitlePrompt] = DefaultPrompts.TitlePrompt,
            [PromptKeys.SummarizePrompt] = DefaultPrompts.DetailedSummarizePrompt,
            [PromptKeys.TagsPrompt] = DefaultPrompts.TagsPrompt,
            [PromptKeys.DocumentEditorPrompt] = DefaultPrompts.DocumentEditorPrompt,
            [PromptKeys.NoContextResponse] = DefaultPrompts.NoContextResponse,
        };

        var existing = await _db.PromptTemplates
            .Where(pt => pt.Scope == PromptScope.Platform)
            .ToListAsync(ct);

        if (existing.Count == 0)
        {
            _logger.LogInformation("Seeding default platform prompts...");

            foreach (var (key, text) in defaults)
            {
                _db.PromptTemplates.Add(new PromptTemplate
                {
                    PromptKey = key,
                    Scope = PromptScope.Platform,
                    TenantId = null,
                    UserId = null,
                    TemplateText = text,
                    MergeStrategy = PromptMergeStrategy.Override,
                    Description = $"Default {key} prompt",
                    IsSystemSeeded = true,
                    LastModifiedBy = "system-seed",
                });
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded {Count} default platform prompts", defaults.Count);
            return;
        }

        // Update any system-seeded prompts whose canonical default text has changed.
        var updated = 0;
        foreach (var prompt in existing.Where(pt => pt.IsSystemSeeded))
        {
            if (defaults.TryGetValue(prompt.PromptKey, out var canonical) && prompt.TemplateText != canonical)
            {
                prompt.TemplateText = canonical;
                prompt.LastModifiedBy = "system-seed";
                updated++;
                _logger.LogInformation("Updated system-seeded prompt '{Key}' to latest default", prompt.PromptKey);
            }
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated {Count} system-seeded platform prompt(s)", updated);
        }
        else
        {
            _logger.LogDebug("Platform prompts up to date — no changes needed");
        }
    }
}
