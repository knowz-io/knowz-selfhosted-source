using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PromptManagementServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly PromptManagementService _svc;
    private readonly PromptResolutionService _resolutionService;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public PromptManagementServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        // Create a real PromptResolutionService with a scope factory that returns our DB
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton(tenantProvider);
        serviceCollection.AddScoped(_ => new SelfHostedDbContext(options, tenantProvider));
        serviceCollection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var sp = serviceCollection.BuildServiceProvider();

        _resolutionService = new PromptResolutionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<PromptResolutionService>>());

        var logger = Substitute.For<ILogger<PromptManagementService>>();
        _svc = new PromptManagementService(_db, _resolutionService, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task SeedPlatformPrompts()
    {
        foreach (var key in PromptKeys.All)
        {
            _db.PromptTemplates.Add(new PromptTemplate
            {
                PromptKey = key,
                Scope = PromptScope.Platform,
                TemplateText = $"Default {key}",
                IsSystemSeeded = true,
                LastModifiedBy = "system-seed",
            });
        }
        await _db.SaveChangesAsync();
    }

    // === Platform CRUD ===

    [Fact]
    public async Task GetPlatformPrompts_ReturnsSixSeededPrompts()
    {
        await SeedPlatformPrompts();

        var result = await _svc.GetPlatformPromptsAsync();

        Assert.Equal(6, result.Count);
        Assert.All(result, p => Assert.Equal(PromptScope.Platform, p.Scope));
    }

    [Fact]
    public async Task UpdatePlatformPrompt_ChangesTextAndClearsSeededFlag()
    {
        await SeedPlatformPrompts();

        var result = await _svc.UpdatePlatformPromptAsync(
            PromptKeys.SystemPrompt, "Updated system prompt", null, "admin@test.com");

        Assert.NotNull(result);
        Assert.Equal("Updated system prompt", result!.TemplateText);
        Assert.False(result.IsSystemSeeded);
        Assert.Equal("admin@test.com", result.LastModifiedBy);
    }

    [Fact]
    public async Task UpdatePlatformPrompt_InvalidKey_ReturnsNull()
    {
        await SeedPlatformPrompts();

        var result = await _svc.UpdatePlatformPromptAsync(
            "InvalidKey", "text", null, "admin@test.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResetPlatformPrompt_RestoresDefaultText()
    {
        await SeedPlatformPrompts();

        // First modify it
        await _svc.UpdatePlatformPromptAsync(
            PromptKeys.SystemPrompt, "Modified text", null, "admin@test.com");

        // Then reset
        var result = await _svc.ResetPlatformPromptAsync(PromptKeys.SystemPrompt, "admin@test.com");

        Assert.NotNull(result);
        Assert.Equal(DefaultPrompts.SystemPrompt, result!.TemplateText);
        Assert.True(result.IsSystemSeeded);
    }

    [Fact]
    public async Task ResetPlatformPrompt_InvalidKey_ReturnsNull()
    {
        var result = await _svc.ResetPlatformPromptAsync("InvalidKey", "admin@test.com");

        Assert.Null(result);
    }

    // === Tenant CRUD ===

    [Fact]
    public async Task UpsertTenantPrompt_CreatesNew()
    {
        var result = await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Tenant prompt",
            PromptMergeStrategy.Override, "Custom desc", "admin@test.com");

        Assert.Equal(PromptScope.Tenant, result.Scope);
        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal("Tenant prompt", result.TemplateText);
        Assert.Equal(PromptMergeStrategy.Override, result.MergeStrategy);
    }

    [Fact]
    public async Task UpsertTenantPrompt_UpdatesExisting()
    {
        // Create first
        await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Original",
            PromptMergeStrategy.Override, null, "admin@test.com");

        // Update
        var result = await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Updated",
            PromptMergeStrategy.Supplement, null, "admin@test.com");

        Assert.Equal("Updated", result.TemplateText);
        Assert.Equal(PromptMergeStrategy.Supplement, result.MergeStrategy);

        // Should only be one tenant prompt row
        var count = await _db.PromptTemplates
            .CountAsync(pt => pt.Scope == PromptScope.Tenant && pt.PromptKey == PromptKeys.SystemPrompt);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertTenantPrompt_InvalidKey_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _svc.UpsertTenantPromptAsync(
                TenantId, "InvalidKey", "text",
                PromptMergeStrategy.Override, null, "admin@test.com"));
    }

    [Fact]
    public async Task GetTenantPrompts_ReturnsOnlyTenantScopeForGivenTenant()
    {
        var otherTenantId = Guid.NewGuid();

        await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Tenant1 prompt",
            PromptMergeStrategy.Override, null, "admin@test.com");

        await _svc.UpsertTenantPromptAsync(
            otherTenantId, PromptKeys.SystemPrompt, "Tenant2 prompt",
            PromptMergeStrategy.Override, null, "admin@test.com");

        var result = await _svc.GetTenantPromptsAsync(TenantId);

        Assert.Single(result);
        Assert.Equal("Tenant1 prompt", result[0].TemplateText);
    }

    [Fact]
    public async Task DeleteTenantPrompt_RemovesExisting()
    {
        await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Tenant prompt",
            PromptMergeStrategy.Override, null, "admin@test.com");

        var deleted = await _svc.DeleteTenantPromptAsync(TenantId, PromptKeys.SystemPrompt);

        Assert.True(deleted);

        var remaining = await _db.PromptTemplates
            .CountAsync(pt => pt.Scope == PromptScope.Tenant && pt.TenantId == TenantId);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task DeleteTenantPrompt_NonExistent_ReturnsFalse()
    {
        var deleted = await _svc.DeleteTenantPromptAsync(TenantId, PromptKeys.SystemPrompt);

        Assert.False(deleted);
    }

    // === User CRUD ===

    [Fact]
    public async Task UpsertUserPrompt_CreatesNewForEligibleKey()
    {
        var result = await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "User supplement", "user@test.com");

        Assert.Equal(PromptScope.User, result.Scope);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal("User supplement", result.TemplateText);
        Assert.Equal(PromptMergeStrategy.Supplement, result.MergeStrategy);
    }

    [Fact]
    public async Task UpsertUserPrompt_RejectsNonEligibleKey()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _svc.UpsertUserPromptAsync(
                UserId, TenantId, PromptKeys.TitlePrompt, "User title hack", "user@test.com"));
    }

    [Fact]
    public async Task UpsertUserPrompt_UpdatesExisting()
    {
        await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "Original user prompt", "user@test.com");

        var result = await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "Updated user prompt", "user@test.com");

        Assert.Equal("Updated user prompt", result.TemplateText);

        var count = await _db.PromptTemplates
            .CountAsync(pt => pt.Scope == PromptScope.User && pt.UserId == UserId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetUserPrompts_ReturnsOnlyUserScopeForGivenUser()
    {
        var otherUserId = Guid.NewGuid();

        await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "User1 prompt", "user1@test.com");

        await _svc.UpsertUserPromptAsync(
            otherUserId, TenantId, PromptKeys.SystemPrompt, "User2 prompt", "user2@test.com");

        var result = await _svc.GetUserPromptsAsync(UserId);

        Assert.Single(result);
        Assert.Equal("User1 prompt", result[0].TemplateText);
    }

    [Fact]
    public async Task DeleteUserPrompt_RemovesExisting()
    {
        await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "User supplement", "user@test.com");

        var deleted = await _svc.DeleteUserPromptAsync(UserId, PromptKeys.SystemPrompt);

        Assert.True(deleted);

        var remaining = await _db.PromptTemplates
            .CountAsync(pt => pt.Scope == PromptScope.User && pt.UserId == UserId);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task DeleteUserPrompt_NonExistent_ReturnsFalse()
    {
        var deleted = await _svc.DeleteUserPromptAsync(UserId, PromptKeys.SystemPrompt);

        Assert.False(deleted);
    }

    // === Resolved view ===

    [Fact]
    public async Task GetResolvedPrompts_CombinesAllLayers()
    {
        await SeedPlatformPrompts();
        await _svc.UpsertTenantPromptAsync(
            TenantId, PromptKeys.SystemPrompt, "Tenant supplement",
            PromptMergeStrategy.Supplement, null, "admin@test.com");
        await _svc.UpsertUserPromptAsync(
            UserId, TenantId, PromptKeys.SystemPrompt, "User supplement", "user@test.com");

        var result = await _svc.GetResolvedPromptsAsync(TenantId, UserId);

        Assert.Contains("Default SystemPrompt", result.SystemPrompt);
        Assert.Contains("Tenant supplement", result.SystemPrompt);
        Assert.Contains("User supplement", result.SystemPrompt);

        // Non-overridden keys should still be the platform default
        Assert.Equal("Default TitlePrompt", result.TitlePrompt);
    }

    /// <summary>NullLogger implementation for test DI container.</summary>
    private class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
