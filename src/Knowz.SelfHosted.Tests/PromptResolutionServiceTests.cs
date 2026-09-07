using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

public class PromptResolutionServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    // === MergePrompts: Platform-only ===

    [Fact]
    public void MergePrompts_PlatformOnly_ReturnsAllPlatformPrompts()
    {
        var templates = PromptKeys.All.Select(key => new PromptTemplate
        {
            PromptKey = key,
            Scope = PromptScope.Platform,
            TemplateText = $"Platform-{key}",
        }).ToList();

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("Platform-SystemPrompt", result.SystemPrompt);
        Assert.Equal("Platform-TitlePrompt", result.TitlePrompt);
        Assert.Equal("Platform-SummarizePrompt", result.SummarizePrompt);
        Assert.Equal("Platform-TagsPrompt", result.TagsPrompt);
        Assert.Equal("Platform-DocumentEditorPrompt", result.DocumentEditorPrompt);
        Assert.Equal("Platform-NoContextResponse", result.NoContextResponse);
    }

    [Fact]
    public void MergePrompts_EmptyTemplates_FallsBackToDefaults()
    {
        var result = PromptResolutionService.MergePrompts(new List<PromptTemplate>());

        Assert.Equal(DefaultPrompts.SystemPrompt, result.SystemPrompt);
        Assert.Equal(DefaultPrompts.TitlePrompt, result.TitlePrompt);
        Assert.Equal(DefaultPrompts.DetailedSummarizePrompt, result.SummarizePrompt);
        Assert.Equal(DefaultPrompts.TagsPrompt, result.TagsPrompt);
        Assert.Equal(DefaultPrompts.DocumentEditorPrompt, result.DocumentEditorPrompt);
        Assert.Equal(DefaultPrompts.NoContextResponse, result.NoContextResponse);
    }

    // === MergePrompts: Tenant Override ===

    [Fact]
    public void MergePrompts_TenantOverride_ReplacesPromptText()
    {
        var templates = new List<PromptTemplate>
        {
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform system prompt",
            },
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Tenant,
                TenantId = TenantId,
                TemplateText = "Tenant override prompt",
                MergeStrategy = PromptMergeStrategy.Override,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("Tenant override prompt", result.SystemPrompt);
    }

    // === MergePrompts: Tenant Supplement ===

    [Fact]
    public void MergePrompts_TenantSupplement_AppendsToPromptText()
    {
        var templates = new List<PromptTemplate>
        {
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform base",
            },
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Tenant,
                TenantId = TenantId,
                TemplateText = "Tenant supplement",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("Platform base\n\nTenant supplement", result.SystemPrompt);
    }

    // === MergePrompts: User Supplement ===

    [Fact]
    public void MergePrompts_UserSupplement_AppendsToSystemPrompt()
    {
        var templates = new List<PromptTemplate>
        {
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform base",
            },
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.User,
                UserId = UserId,
                TemplateText = "User supplement",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("Platform base\n\nUser supplement", result.SystemPrompt);
    }

    [Fact]
    public void MergePrompts_UserSupplement_IgnoredForNonEligibleKeys()
    {
        var templates = new List<PromptTemplate>
        {
            new()
            {
                PromptKey = PromptKeys.TitlePrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform title prompt",
            },
            new()
            {
                PromptKey = PromptKeys.TitlePrompt,
                Scope = PromptScope.User,
                UserId = UserId,
                TemplateText = "User title supplement — should be ignored",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        // TitlePrompt is NOT user-eligible, so user supplement must be ignored
        Assert.Equal("Platform title prompt", result.TitlePrompt);
        Assert.DoesNotContain("ignored", result.TitlePrompt);
    }

    // === MergePrompts: All layers combined ===

    [Fact]
    public void MergePrompts_AllLayers_MergesCorrectly()
    {
        var templates = new List<PromptTemplate>
        {
            // Platform
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform base",
            },
            // Tenant supplement
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Tenant,
                TenantId = TenantId,
                TemplateText = "Tenant addition",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
            // User supplement
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.User,
                UserId = UserId,
                TemplateText = "User addition",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("Platform base\n\nTenant addition\n\nUser addition", result.SystemPrompt);
    }

    [Fact]
    public void MergePrompts_TenantOverridePlusUserSupplement_OverrideThenAppend()
    {
        var templates = new List<PromptTemplate>
        {
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Platform,
                TemplateText = "Platform base — should be replaced",
            },
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.Tenant,
                TenantId = TenantId,
                TemplateText = "Tenant override",
                MergeStrategy = PromptMergeStrategy.Override,
            },
            new()
            {
                PromptKey = PromptKeys.SystemPrompt,
                Scope = PromptScope.User,
                UserId = UserId,
                TemplateText = "User supplement",
                MergeStrategy = PromptMergeStrategy.Supplement,
            },
        };

        var result = PromptResolutionService.MergePrompts(templates);

        // Platform overridden by tenant, then user appended
        Assert.DoesNotContain("Platform base", result.SystemPrompt);
        Assert.Equal("Tenant override\n\nUser supplement", result.SystemPrompt);
    }

    // === MergePrompts: Multiple keys independently resolved ===

    [Fact]
    public void MergePrompts_DifferentKeysResolveIndependently()
    {
        var templates = new List<PromptTemplate>
        {
            new() { PromptKey = PromptKeys.SystemPrompt, Scope = PromptScope.Platform, TemplateText = "SP-Platform" },
            new() { PromptKey = PromptKeys.TitlePrompt, Scope = PromptScope.Platform, TemplateText = "TP-Platform" },
            new() { PromptKey = PromptKeys.SystemPrompt, Scope = PromptScope.Tenant, TenantId = TenantId, TemplateText = "SP-Tenant", MergeStrategy = PromptMergeStrategy.Override },
            // TitlePrompt has no tenant override — stays platform
        };

        var result = PromptResolutionService.MergePrompts(templates);

        Assert.Equal("SP-Tenant", result.SystemPrompt);
        Assert.Equal("TP-Platform", result.TitlePrompt);
    }

    // === GetDefaultPromptSet ===

    [Fact]
    public void GetDefaultPromptSet_ReturnsAllDefaults()
    {
        var result = PromptResolutionService.GetDefaultPromptSet();

        Assert.Equal(DefaultPrompts.SystemPrompt, result.SystemPrompt);
        Assert.Equal(DefaultPrompts.TitlePrompt, result.TitlePrompt);
        Assert.Equal(DefaultPrompts.DetailedSummarizePrompt, result.SummarizePrompt);
        Assert.Equal(DefaultPrompts.TagsPrompt, result.TagsPrompt);
        Assert.Equal(DefaultPrompts.DocumentEditorPrompt, result.DocumentEditorPrompt);
        Assert.Equal(DefaultPrompts.NoContextResponse, result.NoContextResponse);
    }
}
