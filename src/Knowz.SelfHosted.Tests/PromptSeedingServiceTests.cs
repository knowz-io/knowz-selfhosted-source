using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PromptSeedingServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly PromptSeedingService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PromptSeedingServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var logger = Substitute.For<ILogger<PromptSeedingService>>();
        _svc = new PromptSeedingService(_db, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task SeedDefaultPrompts_Creates6PlatformPrompts()
    {
        await _svc.SeedDefaultPromptsAsync();

        var prompts = await _db.PromptTemplates
            .Where(pt => pt.Scope == PromptScope.Platform)
            .ToListAsync();

        Assert.Equal(6, prompts.Count);
        Assert.All(prompts, p =>
        {
            Assert.Equal(PromptScope.Platform, p.Scope);
            Assert.True(p.IsSystemSeeded);
            Assert.Equal("system-seed", p.LastModifiedBy);
            Assert.Null(p.TenantId);
            Assert.Null(p.UserId);
        });
    }

    [Fact]
    public async Task SeedDefaultPrompts_SeedsCorrectPromptTexts()
    {
        await _svc.SeedDefaultPromptsAsync();

        var prompts = await _db.PromptTemplates.ToListAsync();
        var byKey = prompts.ToDictionary(p => p.PromptKey);

        Assert.Equal(DefaultPrompts.SystemPrompt, byKey[PromptKeys.SystemPrompt].TemplateText);
        Assert.Equal(DefaultPrompts.TitlePrompt, byKey[PromptKeys.TitlePrompt].TemplateText);
        Assert.Equal(DefaultPrompts.DetailedSummarizePrompt, byKey[PromptKeys.SummarizePrompt].TemplateText);
        Assert.Equal(DefaultPrompts.TagsPrompt, byKey[PromptKeys.TagsPrompt].TemplateText);
        Assert.Equal(DefaultPrompts.DocumentEditorPrompt, byKey[PromptKeys.DocumentEditorPrompt].TemplateText);
        Assert.Equal(DefaultPrompts.NoContextResponse, byKey[PromptKeys.NoContextResponse].TemplateText);
    }

    [Fact]
    public async Task SeedDefaultPrompts_IsIdempotent_DoesNotDuplicate()
    {
        // Seed twice
        await _svc.SeedDefaultPromptsAsync();
        await _svc.SeedDefaultPromptsAsync();

        var count = await _db.PromptTemplates.CountAsync();

        Assert.Equal(6, count);
    }

    [Fact]
    public async Task SeedDefaultPrompts_SkipsIfPlatformPromptsExist()
    {
        // Manually add one platform prompt
        _db.PromptTemplates.Add(new PromptTemplate
        {
            PromptKey = PromptKeys.SystemPrompt,
            Scope = PromptScope.Platform,
            TemplateText = "Custom platform prompt",
            IsSystemSeeded = false,
            LastModifiedBy = "admin",
        });
        await _db.SaveChangesAsync();

        // Seed should skip because at least one platform prompt exists
        await _svc.SeedDefaultPromptsAsync();

        var count = await _db.PromptTemplates.CountAsync();
        Assert.Equal(1, count); // Only the manually added one

        // The existing prompt should be unchanged
        var prompt = await _db.PromptTemplates.SingleAsync();
        Assert.Equal("Custom platform prompt", prompt.TemplateText);
    }

    [Fact]
    public async Task SeedDefaultPrompts_DoesNotConflictWithTenantPrompts()
    {
        // Add a tenant-scope prompt (should not count as platform seeded)
        _db.PromptTemplates.Add(new PromptTemplate
        {
            PromptKey = PromptKeys.SystemPrompt,
            Scope = PromptScope.Tenant,
            TenantId = TenantId,
            TemplateText = "Tenant prompt",
            MergeStrategy = PromptMergeStrategy.Override,
        });
        await _db.SaveChangesAsync();

        // Seed should proceed because no platform-scope prompts exist
        await _svc.SeedDefaultPromptsAsync();

        var platformCount = await _db.PromptTemplates.CountAsync(pt => pt.Scope == PromptScope.Platform);
        var tenantCount = await _db.PromptTemplates.CountAsync(pt => pt.Scope == PromptScope.Tenant);

        Assert.Equal(6, platformCount);
        Assert.Equal(1, tenantCount);
    }

    [Fact]
    public async Task SeedDefaultPrompts_AllPromptsHaveOverrideMergeStrategy()
    {
        await _svc.SeedDefaultPromptsAsync();

        var prompts = await _db.PromptTemplates.ToListAsync();

        Assert.All(prompts, p => Assert.Equal(PromptMergeStrategy.Override, p.MergeStrategy));
    }
}
