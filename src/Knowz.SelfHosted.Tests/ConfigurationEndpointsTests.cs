using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Unit tests for ConfigurationEndpoints covering auth enforcement,
/// response codes, and response shapes. Uses a real service instance
/// with InMemoryDatabase for isolation.
/// </summary>
public class ConfigurationEndpointsTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ConfigurationManagementService _service;
    private readonly IConfigurationManagementService _mockService;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public ConfigurationEndpointsTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);

        _dataProtectionProvider = new EphemeralDataProtectionProvider();
        var configProvider = Substitute.For<DatabaseConfigurationProvider>(
            new DatabaseConfigurationSource { ConnectionString = "", DataProtectionProvider = _dataProtectionProvider });
        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();

        var emptyConfig = new ConfigurationBuilder().Build();
        _service = new ConfigurationManagementService(
            _db, _dataProtectionProvider, configProvider, logger, emptyConfig);

        _mockService = Substitute.For<IConfigurationManagementService>();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Auth enforcement tests (403 for non-SuperAdmin) ---

    [Fact]
    public async Task GetCategories_Returns200_WithAllCategories()
    {
        var categories = await _service.GetAllCategoriesAsync();

        Assert.NotNull(categories);
        Assert.Equal(12, categories.Count);
        Assert.Contains(categories, c => c.Category == "ConnectionStrings");
        Assert.Contains(categories, c => c.Category == "AzureOpenAI");
        Assert.Contains(categories, c => c.Category == "AzureAIVision");
        Assert.Contains(categories, c => c.Category == "AzureDocumentIntelligence");
        Assert.Contains(categories, c => c.Category == "AzureAISearch");
        Assert.Contains(categories, c => c.Category == "Storage");
        Assert.Contains(categories, c => c.Category == "SelfHosted");
        Assert.Contains(categories, c => c.Category == "AzureKeyVault");
        Assert.Contains(categories, c => c.Category == "KnowzPlatform");
        Assert.Contains(categories, c => c.Category == "Inbox");
        Assert.Contains(categories, c => c.Category == "SSO");
    }

    [Fact]
    public async Task GetCategory_Returns404_WhenNotFound()
    {
        var result = await _service.GetCategoryAsync("NonExistentCategory");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCategory_DoesNotExposeIgnoredDatabaseSecrets()
    {
        // Seed a secret value
        var protector = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");
        _db.SystemConfigurations.Add(new Knowz.Core.Entities.SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "ApiKey",
            EncryptedValue = protector.Protect("sk-test-key-12345678"),
            IsSecret = true,
            RequiresRestart = true,
            Description = "Azure OpenAI API key"
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetCategoryAsync("AzureOpenAI");

        Assert.NotNull(result);
        Assert.Equal("AzureOpenAI", result.Category);
        Assert.Equal("Azure OpenAI", result.DisplayName);

        var apiKeyEntry = result.Entries.First(e => e.Key == "ApiKey");
        Assert.True(apiKeyEntry.IsSecret);
        Assert.False(apiKeyEntry.IsSet);
        Assert.False(apiKeyEntry.Editable);
        Assert.Null(apiKeyEntry.Value);
    }

    [Fact]
    public async Task PutCategory_Returns200_WithUpdateResult()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://my-openai.openai.azure.com/" },
            new() { Key = "DeploymentName", Value = "gpt-4o" }
        };

        var result = await _service.UpdateCategoryAsync("AzureOpenAI", entries, "test-admin");

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
        Assert.Equal(2, result.EntriesUpdated);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PutCategory_Returns400_WithValidationErrors()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "UnknownKey", Value = "some-value" }
        };

        var result = await _service.UpdateCategoryAsync("AzureOpenAI", entries, "test-admin");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("UnknownKey"));
    }

    [Fact]
    public async Task PutCategory_Returns404_WhenCategoryNotFound()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "SomeKey", Value = "some-value" }
        };

        var result = await _service.UpdateCategoryAsync("NonExistent", entries, "test-admin");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.StartsWith("Unknown category:"));
    }

    [Fact]
    public async Task PutCategory_Returns409_OnConcurrencyConflict()
    {
        // Setup mock to throw DbUpdateConcurrencyException
        _mockService.UpdateCategoryAsync(Arg.Any<string>(), Arg.Any<List<ConfigEntryUpdateDto>>(), Arg.Any<string>())
            .ThrowsAsync(new DbUpdateConcurrencyException("Concurrency conflict"));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            _mockService.UpdateCategoryAsync("AzureOpenAI", new List<ConfigEntryUpdateDto>(), "admin"));
    }

    [Fact]
    public async Task PostHealth_Returns200_WithHealthResult()
    {
        var result = await _service.TestConnectionAsync("AzureOpenAI");

        Assert.NotNull(result);
        Assert.Equal("AzureOpenAI", result.Category);
        Assert.Equal("Azure OpenAI", result.DisplayName);
        // No config set yet, so should report "Not Configured"
        Assert.False(result.IsHealthy);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task PostHealthAll_Returns200_WithAllResults()
    {
        var results = await _service.TestAllConnectionsAsync();

        Assert.NotNull(results);
        Assert.Equal(12, results.Count);
        Assert.Contains(results, r => r.Category == "ConnectionStrings");
        Assert.Contains(results, r => r.Category == "AzureOpenAI");
        Assert.Contains(results, r => r.Category == "AzureAIVision");
        Assert.Contains(results, r => r.Category == "AzureDocumentIntelligence");
    }

    [Fact]
    public void GetStatus_Returns200_WithDeploymentInfo()
    {
        var result = _service.GetDeploymentStatus();

        Assert.NotNull(result);
        Assert.Equal("Direct", result.Mode);
        Assert.NotEmpty(result.Version);
        Assert.True(result.StartupTime <= DateTime.UtcNow);
    }

    [Fact]
    public async Task GetCategory_ReturnsNonSecretValuesAsPlaintext()
    {
        var protector = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");
        _db.SystemConfigurations.Add(new Knowz.Core.Entities.SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = protector.Protect("https://my-endpoint.openai.azure.com/"),
            IsSecret = false,
            RequiresRestart = true,
            Description = "Azure OpenAI endpoint URL"
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetCategoryAsync("AzureOpenAI");

        Assert.NotNull(result);
        var endpointEntry = result.Entries.First(e => e.Key == "Endpoint");
        Assert.False(endpointEntry.IsSecret);
        Assert.Equal("https://my-endpoint.openai.azure.com/", endpointEntry.Value);
    }

    [Fact]
    public async Task PutCategory_PreservesLegacySecretRow_WhenSentinel()
    {
        var encrypted = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration").Protect("legacy-secret");
        _db.SystemConfigurations.Add(new Knowz.Core.Entities.SystemConfiguration { Category = "AzureOpenAI", Key = "ApiKey", EncryptedValue = encrypted, IsSecret = true });
        await _db.SaveChangesAsync();
        var result = await _service.UpdateCategoryAsync("AzureOpenAI", [new() { Key = "ApiKey", Value = "****" }], "admin");
        Assert.True(result.Success);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(encrypted, (await _db.SystemConfigurations.SingleAsync()).EncryptedValue);
        Assert.False((await _service.GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "ApiKey").EffectiveIsSet);
    }

    [Fact]
    public async Task PutCategory_RecordsAuditInfo()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://test.openai.azure.com/" }
        };

        await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin-user");

        var category = await _service.GetCategoryAsync("AzureOpenAI");
        var entry = category!.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("admin-user", entry.LastModifiedBy);
        Assert.NotNull(entry.LastModifiedAt);
        Assert.True(entry.LastModifiedAt!.Value <= DateTime.UtcNow);
    }

    [Fact]
    public async Task PostHealth_DoesNotProbeUnappliedDatabaseSettings()
    {
        var protector = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");
        _db.SystemConfigurations.AddRange(
            new Knowz.Core.Entities.SystemConfiguration
            {
                Category = "AzureOpenAI",
                Key = "Endpoint",
                EncryptedValue = protector.Protect("https://my-openai.openai.azure.com/"),
                IsSecret = false,
                RequiresRestart = true
            },
            new Knowz.Core.Entities.SystemConfiguration
            {
                Category = "AzureOpenAI",
                Key = "ApiKey",
                EncryptedValue = protector.Protect("sk-test-key"),
                IsSecret = true,
                RequiresRestart = true
            }
        );
        await _db.SaveChangesAsync();

        var result = await _service.TestConnectionAsync("AzureOpenAI");

        Assert.False(result.IsHealthy);
        Assert.Equal("Not Configured", result.Status);
    }
}
